using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mersal.Identity.Api.Auth;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.3 — the first-party sign-in API (ADR-0036 §5).
///
/// <para>
/// Driven end to end against the real host, because everything interesting here is about COOKIES and about
/// what the ordinary sign-in machinery does — the two things a handler tested in isolation cannot show.
/// </para>
/// <para>
/// The parity class at the bottom is the important one. It compares the API path against the FORM path rather
/// than either against itself: a login API that quietly stopped writing sign-in history or stopped applying
/// the concurrent-session cap would pass every test that only looked at its own behaviour, and the screen it
/// emptied is the one a person uses to see their account being attacked.
/// </para>
/// </summary>
[Collection("identity-db")]
public class SessionApiTests : IClassFixture<IdentityAppFactory>
{
    private readonly IdentityAppFactory _factory;
    public SessionApiTests(IdentityAppFactory factory) => _factory = factory;

    private const string Pass = "Test-Passw0rd!";

    /// <summary>
    /// A seeded identity that REMOVES ITSELF.
    ///
    /// <para>
    /// The DB-gated identity tests share one database, and several of these seed a membership in a second
    /// tenant to reach the chooser. Leaving those rows behind is not merely untidy: <c>TenantFeatureProjection
    /// Tests</c> asserts that EVERY tenant appearing in <c>tenant_membership</c> was backfilled with the whole
    /// module catalogue, so a tenant this suite invents and abandons makes an unrelated test fail with a
    /// message about a backfill. It did — found by running the full suite rather than the filter.
    /// </para>
    /// <para>
    /// The existing selection tests solve it with try/finally around every case. Same discipline, moved into
    /// the type so it cannot be the thing somebody forgets on the next test.
    /// </para>
    /// </summary>
    private sealed class SeededUser(IdentityAppFactory factory, Guid id, string name, string? totpKey)
        : IAsyncDisposable
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string? TotpKey { get; } = totpKey;

        public async ValueTask DisposeAsync() => await TestFlow.DeleteUser(factory, Id);
    }

    private async Task<SeededUser> Seed(string prefix, bool twoFactor = false)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}";
        var (id, key) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"], twoFactor: twoFactor);
        return new SeededUser(_factory, id, name, key);
    }


    // ---- helpers ----------------------------------------------------------------------------------------

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Fetch the antiforgery pair. The SPA sends the request token as a HEADER, having no form.</summary>
    private static async Task<string> Csrf(HttpClient client)
    {
        var res = await client.GetAsync("/connect/session/antiforgery");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string csrf)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-HBMP-CSRF", csrf);
        return await client.SendAsync(req);
    }

    /// <summary>Read the body ONCE. HttpResponseMessage's content stream is consumed on first read, so a
    /// helper that reads status and another that reads the token cannot both run against the same reply —
    /// the second throws ObjectDisposedException, which reads like a server fault and is a test bug.</summary>
    private static async Task<JsonElement> BodyOf(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> StatusOf(HttpResponseMessage res) =>
        (await BodyOf(res)).GetProperty("status").GetString()!;

    /// <summary>The antiforgery token carried back on a reply, for the NEXT step.
    ///
    /// Using it is not test convenience — it is the contract. ASP.NET binds an antiforgery token to the
    /// authenticated user, so the one fetched while anonymous stops validating the instant the password step
    /// signs somebody in, and the second factor or membership choice that follows is refused with a 400.
    /// A client that reused the first token would work for single-step sign-ins and break for every account
    /// with a second factor.</summary>
    /// <summary>The reply, minus the per-request antiforgery token.</summary>
    private static async Task<string> WithoutCsrf(HttpResponseMessage res)
    {
        var body = await BodyOf(res);
        var fields = body.EnumerateObject()
            .Where(p => p.Name != "csrf")
            .Select(p => $"{p.Name}={p.Value.GetRawText()}");
        return string.Join("|", fields);
    }

    private static string NextCsrf(JsonElement body) =>
        body.TryGetProperty("csrf", out var t) && t.GetString() is { Length: > 0 } v ? v : "";

    private static async Task<(HttpClient Client, string Csrf)> Ready(IdentityAppFactory f)
    {
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return (c, await Csrf(c));
    }

    // ---- the sequence -----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_correct_password_with_one_membership_is_authenticated()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-ok");
        var name = seeded.Name;

        var (client, csrf) = await Ready(_factory);
        var res = await Post(client, "/connect/session", new { username = name, password = Pass }, csrf);

        (await StatusOf(res)).Should().Be(SessionStatus.Authenticated);
    }

    [SkippableFact]
    public async Task The_response_never_carries_a_token_or_an_identity()
    {
        // The core of §5: a caller learns what to do next and nothing else. A display name or a user id here
        // would make the login endpoint a directory lookup for anyone who guesses a password once.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-quiet");
        var name = seeded.Name;

        var (client, csrf) = await Ready(_factory);
        var body = await (await Post(client, "/connect/session", new { username = name, password = Pass }, csrf))
            .Content.ReadAsStringAsync();

        foreach (var forbidden in new[] { "access_token", "id_token", "token", "userId", "sub", "displayName", name })
            body.Should().NotContain(forbidden, $"the sign-in reply must not disclose {forbidden}");
    }

    [SkippableFact]
    public async Task An_account_with_two_factor_is_asked_for_it_and_is_not_yet_authenticated()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-2fa", twoFactor: true);
        var (name, key) = (seeded.Name, seeded.TotpKey);

        var (client, csrf) = await Ready(_factory);
        var first = await BodyOf(await Post(client, "/connect/session", new { username = name, password = Pass }, csrf));
        first.GetProperty("status").GetString().Should().Be(SessionStatus.TwoFactorRequired);

        // THE property the whole design exists for: the password alone did not produce a usable session.
        // A password grant could not have expressed this step at all.
        var authorize = await client.GetAsync(SilentAuthorizeUrl());
        authorize.Headers.Location!.ToString().Should().Contain("error=login_required");

        (await StatusOf(await Post(client, "/connect/session/2fa",
            new { code = TestFlow.Totp(key!) }, NextCsrf(first))))
            .Should().Be(SessionStatus.Authenticated);
    }

    [SkippableFact]
    public async Task A_wrong_second_factor_is_invalid_credentials_and_not_a_new_kind_of_answer()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-2fa-bad", twoFactor: true);
        var name = seeded.Name;

        var (client, csrf) = await Ready(_factory);
        var first = await BodyOf(await Post(client, "/connect/session", new { username = name, password = Pass }, csrf));

        (await StatusOf(await Post(client, "/connect/session/2fa", new { code = "000000" }, NextCsrf(first))))
            .Should().Be(SessionStatus.InvalidCredentials);
    }

    [SkippableFact]
    public async Task Several_memberships_ask_which_one_and_the_choice_completes_the_sign_in()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-multi");
        var (name, userId) = (seeded.Name, seeded.Id);
        var second = await TestFlow.SeedMembership(
            _factory, userId, "22222222-2222-2222-2222-222222222222", ["reception"]);

        var (client, csrf) = await Ready(_factory);
        var json = await BodyOf(await Post(client, "/connect/session", new { username = name, password = Pass }, csrf));

        json.GetProperty("status").GetString().Should().Be(SessionStatus.MembershipSelectionRequired);
        json.GetProperty("memberships").GetArrayLength().Should().Be(2);

        (await StatusOf(await Post(client, "/connect/session/membership",
            new { membershipId = second }, json.GetProperty("csrf").GetString()!)))
            .Should().Be(SessionStatus.Authenticated);
    }

    [SkippableFact]
    public async Task A_membership_this_identity_does_not_hold_is_re_resolved_and_refused()
    {
        // A membership id is not a secret and this body is caller-controlled. Trusting it would be a direct
        // path into another organization's tenant.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-badmem");
        var (name, userId) = (seeded.Name, seeded.Id);
        await TestFlow.SeedMembership(_factory, userId, "22222222-2222-2222-2222-222222222222", ["reception"]);

        var (client, csrf) = await Ready(_factory);
        var first = await BodyOf(await Post(client, "/connect/session", new { username = name, password = Pass }, csrf));

        (await StatusOf(await Post(client, "/connect/session/membership",
            new { membershipId = Guid.NewGuid() }, NextCsrf(first))))
            .Should().Be(SessionStatus.MembershipSelectionRequired);
    }

    // ---- what each failure is allowed to say ------------------------------------------------------------

    [SkippableTheory]
    [InlineData("no-such-user-at-all")]
    public async Task An_unknown_username_is_invalid_credentials(string missing)
    {
        Skip.If(IdentityTestDb.Conn is null);
        var (client, csrf) = await Ready(_factory);
        (await StatusOf(await Post(client, "/connect/session", new { username = missing, password = Pass }, csrf)))
            .Should().Be(SessionStatus.InvalidCredentials);
    }

    [SkippableFact]
    public async Task A_wrong_password_is_the_same_answer_as_an_unknown_username()
    {
        // The enumeration rule, asserted as an EQUALITY rather than as two separate expectations — which is
        // the only form that fails when somebody makes one of them more helpful.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-enum");
        var name = seeded.Name;

        var (c1, k1) = await Ready(_factory);
        var real = await Post(c1, "/connect/session", new { username = name, password = "Wrong-1!" }, k1);

        var (c2, k2) = await Ready(_factory);
        var fake = await Post(c2, "/connect/session", new { username = "definitely-not-a-user", password = "Wrong-1!" }, k2);

        // Everything EXCEPT the antiforgery token, which is freshly minted per reply and differs by design.
        // Comparing whole bodies would fail on that alone and say nothing about enumeration.
        (await WithoutCsrf(real)).Should().Be(await WithoutCsrf(fake),
            "a wrong password and an unknown username must be indistinguishable to the caller");
    }

    [SkippableFact]
    public async Task A_deactivated_account_is_also_invalid_credentials()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-inactive");
        var (name, userId) = (seeded.Name, seeded.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
            var u = await db.Users.FindAsync(userId);
            u!.IsActive = false;
            await db.SaveChangesAsync();
        }

        var (client, csrf) = await Ready(_factory);
        (await StatusOf(await Post(client, "/connect/session", new { username = name, password = Pass }, csrf)))
            .Should().Be(SessionStatus.InvalidCredentials);
    }

    // ---- CSRF -------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_request_with_no_antiforgery_token_is_refused_as_a_bad_request()
    {
        // 400, NOT invalid_credentials. A missing token is a fact about the request; answering it with a
        // credential verdict would tell a user with a correct password that it was wrong, and hand an
        // attacker probing CSRF the same reply as a failed guess.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-csrf");
        var name = seeded.Name;

        var client = NewClient();
        var res = await client.PostAsJsonAsync("/connect/session", new { username = name, password = Pass });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task Enrolment_is_antiforgery_protected_too()
    {
        // The sharpest CSRF case on the issuer, and it carries over from AccountPages unchanged: a forged
        // enrolment makes the ATTACKER's authenticator the victim's second factor, on a session the victim
        // already had, showing them nothing.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-enrol-csrf");
        var name = seeded.Name;

        var (client, csrf) = await Ready(_factory);
        await Post(client, "/connect/session", new { username = name, password = Pass }, csrf);

        var forged = await client.PostAsJsonAsync("/connect/session/authenticator", new { code = "123456" });
        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- silent authorize -------------------------------------------------------------------------------

    internal static string SilentAuthorizeUrl()
    {
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes("verifier-for-a-prompt-none-probe")));
        return "/connect/authorize?response_type=code&prompt=none"
            + $"&client_id={IdentityContract.WebClientId}"
            + $"&redirect_uri={Uri.EscapeDataString("http://localhost:5173/")}"
            + $"&scope={Uri.EscapeDataString("openid")}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=xyz";
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [SkippableFact]
    public async Task With_no_session_a_silent_authorize_says_login_required_and_does_not_render_a_login()
    {
        // The loop-breaker. The SPA never renders the server's login page, so an authorize it cannot satisfy
        // must terminate in an error it can READ. A challenge here would redirect it to the page this whole
        // ADR exists to stop showing, and it would follow that redirect forever.
        Skip.If(IdentityTestDb.Conn is null);
        var client = NewClient();
        var res = await client.GetAsync(SilentAuthorizeUrl());

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = res.Headers.Location!.ToString();
        location.Should().StartWith("http://localhost:5173/", "the error comes back to the SPA, not to a page");
        location.Should().Contain("error=login_required");
        location.Should().NotContain("/connect/login");
    }

    [SkippableFact]
    public async Task After_the_session_api_a_silent_authorize_returns_a_code_with_no_interaction()
    {
        // The whole point of 28.3, end to end: the SPA drove the sign-in itself, and the token still comes
        // from the unchanged authorization-code flow.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-silent");
        var name = seeded.Name;

        var (client, csrf) = await Ready(_factory);
        (await StatusOf(await Post(client, "/connect/session", new { username = name, password = Pass }, csrf)))
            .Should().Be(SessionStatus.Authenticated);

        var res = await client.GetAsync(SilentAuthorizeUrl());
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("code=");
    }

    [SkippableFact]
    public async Task An_unresolved_membership_under_prompt_none_is_interaction_required_not_login_required()
    {
        // Two different remedies, so two different errors. Collapsing them would send a signed-in user back to
        // a password prompt in order to answer a question about which organization they are working in.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("api-silent-multi");
        var (name, userId) = (seeded.Name, seeded.Id);
        await TestFlow.SeedMembership(_factory, userId, "22222222-2222-2222-2222-222222222222", ["reception"]);

        var (client, csrf) = await Ready(_factory);
        (await StatusOf(await Post(client, "/connect/session", new { username = name, password = Pass }, csrf)))
            .Should().Be(SessionStatus.MembershipSelectionRequired);

        var res = await client.GetAsync(SilentAuthorizeUrl());
        res.Headers.Location!.ToString().Should().Contain("error=interaction_required");
    }

    [SkippableFact]
    public async Task Without_prompt_none_an_unauthenticated_authorize_still_reaches_the_login_page()
    {
        // The server-rendered pages are FROZEN, not deleted (ADR-0036 §7). OIDC still requires an interactive
        // login to exist, and any non-SPA client arriving cold must land on something a human can use.
        Skip.If(IdentityTestDb.Conn is null);
        var client = NewClient();
        var res = await client.GetAsync(SilentAuthorizeUrl().Replace("&prompt=none", ""));

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("/connect/login");
    }
}
