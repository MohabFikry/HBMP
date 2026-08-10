using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.8 — signing in with an email address, and the account lifecycle that makes one usable.
///
/// <para>
/// ============================================================================================================
/// WHAT THESE PROVE
/// ============================================================================================================
/// Four things that were missing rather than wrong:
///   * an account can be reached by the credential its owner actually remembers (their address);
///   * an address identifies exactly ONE account, or "whose password is being checked" has no answer;
///   * a deprovisioned account has a way back that is not an UPDATE typed into psql;
///   * a signed-in person can change a password they already know, without pretending to have lost it.
/// </para>
///
/// <para>
/// ============================================================================================================
/// AND ONE THING THAT MUST NOT HAVE CHANGED
/// ============================================================================================================
/// Two lookups where there was one is two ways to ask "does this address have an account". The refusal must
/// stay indistinguishable across unknown-address, unknown-username, wrong-password and deactivated — which is
/// what <see cref="An_unknown_address_is_refused_exactly_like_a_wrong_password"/> holds in place. It is the
/// test most worth keeping if the others are ever rewritten.
/// </para>
/// </summary>
[Collection("identity-db")]
public class EmailLoginAndLifecycleTests : IClassFixture<IdentityAppFactory>
{
    private readonly IdentityAppFactory _factory;
    public EmailLoginAndLifecycleTests(IdentityAppFactory factory) => _factory = factory;

    private const string Pass = "Test-Passw0rd!";
    private const string Scope = "openid admin:read admin:write";

    // ---- sign-in ---------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_account_signs_in_with_its_email_address()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"emaillogin-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"]);
        try
        {
            // TestFlow seeds `{username}@example.org`, which is the address a real account would carry.
            var state = await SignIn($"{name}@example.org", Pass);
            state.Should().NotBe("invalid_credentials", "the address is a credential now, not just a contact field");
        }
        finally { await TestFlow.DeleteUser(_factory, id); }
    }

    [SkippableFact]
    public async Task The_username_still_works_for_the_accounts_that_only_have_one()
    {
        Skip.If(IdentityTestDb.Conn is null);
        // The whole reason email is a FALLBACK-to-username lookup rather than a replacement: service accounts
        // and every seeded fixture predate 28.8, and a rename would have locked all of them out at once.
        var name = $"namelogin-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"]);
        try
        {
            var state = await SignIn(name, Pass);
            state.Should().NotBe("invalid_credentials");
        }
        finally { await TestFlow.DeleteUser(_factory, id); }
    }

    [SkippableFact]
    public async Task An_unknown_address_is_refused_exactly_like_a_wrong_password()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"oracle-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"]);
        try
        {
            // THE ENUMERATION ORACLE TEST. If the address lookup ever reports "no such account" differently
            // from "wrong password", the sign-in form becomes a way to ask whether a given person works here
            // — which for a refugee-health platform is a question about the person, not about the account.
            var unknown = await SignIn($"nobody-{Guid.NewGuid():N}@example.org", Pass);
            var wrongPassword = await SignIn($"{name}@example.org", "Wrong-Passw0rd!");

            unknown.Should().Be("invalid_credentials");
            wrongPassword.Should().Be("invalid_credentials");
            unknown.Should().Be(wrongPassword, "one answer, or the pair of them is an oracle");
        }
        finally { await TestFlow.DeleteUser(_factory, id); }
    }

    // ---- creation --------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_account_cannot_be_created_without_an_address_to_reach_it_at()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("create-noemail");
        var client = await AdminClient(admin);

        var res = await client.PostAsJsonAsync("/identity/admin/users", new
        {
            username = $"noemail-{Guid.NewGuid():N}", displayName = "No Email",
            tenantId = TestFlow.TenantA, roles = new[] { "reception" },
        });

        // 422 and not 400: the request is well-formed and the rule it breaks is ours. An account with no
        // address can neither sign in by address nor be sent a reset link — it is unreachable from birth.
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("email-required");
    }

    [SkippableFact]
    public async Task An_address_already_in_use_is_a_conflict_and_says_so()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("create-dupe");
        var client = await AdminClient(admin);
        var shared = $"shared-{Guid.NewGuid():N}@example.org";

        var first = await client.PostAsJsonAsync("/identity/admin/users", new
        {
            username = $"dupe-a-{Guid.NewGuid():N}", displayName = "A", email = shared,
            tenantId = TestFlow.TenantA, roles = new[] { "reception" },
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/identity/admin/users", new
        {
            username = $"dupe-b-{Guid.NewGuid():N}", displayName = "B", email = shared,
            tenantId = TestFlow.TenantA, roles = new[] { "reception" },
        });

        // 409, not a generic "create-failed" — an administrator has to be able to tell "that address is
        // taken" from "that password was rejected", and Identity's own validation reports both the same way.
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain("email-taken");
    }

    [SkippableFact]
    public async Task Creating_an_account_sends_an_invitation_and_returns_no_credential()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("create-invite");
        var client = await AdminClient(admin);
        CapturedEmail.Instance.Clear();

        var address = $"invited-{Guid.NewGuid():N}@example.org";
        var res = await client.PostAsJsonAsync("/identity/admin/users", new
        {
            username = $"invited-{Guid.NewGuid():N}", displayName = "Invited", email = address,
            tenantId = TestFlow.TenantA, roles = new[] { "reception" },
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("resetLinkSent");
        // 28.7's rule, applied to creation: there must be no moment at which the administrator knows the
        // credential. The response is where one would leak if the endpoint ever started returning one.
        body.Should().NotContain("password");

        CapturedEmail.Instance.Sent.Should().Contain(e => e.To == address,
            "an account nobody was told about is an account nobody can use");
    }

    // ---- lifecycle -------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_deactivated_account_can_be_brought_back()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("reactivate");
        var client = await AdminClient(admin);
        var name = $"returner-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"]);
        try
        {
            (await client.PostAsJsonAsync($"/identity/admin/users/{id}/deactivate", new { }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await SignIn(name, Pass)).Should().Be("invalid_credentials", "a deprovisioned account cannot sign in");

            (await client.PostAsJsonAsync($"/identity/admin/users/{id}/reactivate", new { }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // The point of the endpoint: before it, the only remedy was an UPDATE run by hand against the
            // database — unaudited, and exactly the kind of access this service exists to remove the need for.
            (await SignIn(name, Pass)).Should().NotBe("invalid_credentials");
        }
        finally { await TestFlow.DeleteUser(_factory, id); }
    }

    [SkippableFact]
    public async Task An_address_can_be_corrected_without_creating_a_second_account()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("update-email");
        var client = await AdminClient(admin);
        var name = $"typo-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"]);
        try
        {
            var fixedAddress = $"fixed-{Guid.NewGuid():N}@example.org";
            var res = await client.PostAsJsonAsync($"/identity/admin/users/{id}", new { email = fixedAddress });
            res.StatusCode.Should().Be(HttpStatusCode.OK);

            // The address is the sign-in credential, so a wrong one is not cosmetic — and before this the
            // only fix was to deprovision and start again, losing the continuity of the person.
            (await SignIn(fixedAddress, Pass)).Should().NotBe("invalid_credentials");
        }
        finally { await TestFlow.DeleteUser(_factory, id); }
    }

    // ---- self-service ----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_person_changes_their_own_password_and_every_other_session_ends()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"selfchange-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"]);
        try
        {
            var token = await TestFlow.AuthCodeToken(_factory, name, Pass, null, "openid");
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            const string next = "Test-Passw0rd!2";
            var res = await client.PostAsJsonAsync("/identity/me/password",
                new { currentPassword = Pass, newPassword = next });
            res.StatusCode.Should().Be(HttpStatusCode.OK);

            // ASSERTED FIRST, before the verification sign-ins below.
            //
            // The most common reason to change a password is believing somebody else has it, and a change
            // that leaves their session live answers that fear with nothing. But a successful sign-in OPENS
            // a session, so checking this after the two below would count the one this test had just made
            // and report the revocation as broken — which is how this assertion failed the first time. The
            // ordering is the test, not an accident of it.
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
                var live = await db.Sessions.AsNoTracking()
                    .CountAsync(s => s.UserId == id && s.RevokedAt == null);
                live.Should().Be(0, "sign-in sessions opened before the change must not survive it");
            }

            (await SignIn(name, next)).Should().NotBe("invalid_credentials");
            (await SignIn(name, Pass)).Should().Be("invalid_credentials", "the old password is spent");
        }
        finally { await TestFlow.DeleteUser(_factory, id); }
    }

    [SkippableFact]
    public async Task Changing_a_password_requires_knowing_the_current_one()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"nocurrent-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"]);
        try
        {
            var token = await TestFlow.AuthCodeToken(_factory, name, Pass, null, "openid");
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            // A live token proves somebody has the DEVICE. Without this check an unattended unlocked
            // workstation is a permanent account takeover, and the owner's own recovery path is the only
            // thing that would ever tell them.
            var res = await client.PostAsJsonAsync("/identity/me/password",
                new { currentPassword = "Not-The-Passw0rd!", newPassword = "Test-Passw0rd!3" });
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

            (await SignIn(name, Pass)).Should().NotBe("invalid_credentials", "nothing changed");
        }
        finally { await TestFlow.DeleteUser(_factory, id); }
    }

    // ---- harness ---------------------------------------------------------------------------------------

    /// <summary>
    /// Drive `POST /connect/session` the way the SPA does, and return the status it reports.
    ///
    /// <para>The antiforgery pair is REAL, not bypassed: the cookie half comes back from `/antiforgery` and
    /// has to be carried into the POST, or the endpoint answers with a 400 problem document instead of a
    /// status. `X-HBMP-CSRF` is the header name `IssuerSetup` configures — sending the framework default
    /// gets a refusal that looks exactly like a rejected password, which is how this helper was wrong the
    /// first time.</para>
    /// </summary>
    private async Task<string> SignIn(string login, string password)
    {
        // A cookie container is what makes this work: `GetAndStoreTokens` sets the cookie half on the
        // response, and the POST must send it back alongside the header half.
        var client = _factory.CreateDefaultClient(new CookieHandler());
        var tokenBody = await (await client.GetAsync("/connect/session/antiforgery")).Content.ReadAsStringAsync();
        var token = System.Text.Json.JsonDocument.Parse(tokenBody).RootElement.GetProperty("token").GetString();
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/session")
        {
            Content = JsonContent.Create(new { username = login, password }),
        };
        req.Headers.Add("X-HBMP-CSRF", token);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        var root = System.Text.Json.JsonDocument.Parse(body).RootElement;
        // A `status` that is a NUMBER is an RFC-7807 problem document, not a session status — the two
        // collide on the property name. Reported as itself rather than silently read as a refusal, which
        // would turn every harness fault into a passing "credentials rejected" assertion.
        if (root.TryGetProperty("status", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.Number)
            throw new InvalidOperationException($"sign-in returned a problem document, not a status: {body}");
        return root.GetProperty("status").GetString() ?? "";
    }

    /// <summary>Carries cookies across the antiforgery GET and the sign-in POST.</summary>
    private sealed class CookieHandler : DelegatingHandler
    {
        private readonly System.Net.CookieContainer _jar = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var cookies = _jar.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookies)) request.Headers.Add("Cookie", cookies);

            var response = await base.SendAsync(request, ct);

            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (var c in set) _jar.SetCookies(uri, c);
            return response;
        }
    }

    private sealed class Seeded(IdentityAppFactory factory, Guid id, string name, string? totpKey) : IAsyncDisposable
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string? TotpKey { get; } = totpKey;
        public async ValueTask DisposeAsync() => await TestFlow.DeleteUser(factory, Id);
    }

    /// <summary>An administrator the `/identity/admin` group will accept — which means WITH a second factor:
    /// that group requires an MFA session, not merely the scope.</summary>
    private async Task<Seeded> Admin(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}";
        var (id, key) = await TestFlow.SeedUser(_factory, name, Pass, ["super_admin"], twoFactor: true);
        return new Seeded(_factory, id, name, key);
    }

    private async Task<HttpClient> AdminClient(Seeded admin)
    {
        var token = await TestFlow.AuthCodeToken(_factory, admin.Name, Pass, admin.TotpKey, Scope);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}
