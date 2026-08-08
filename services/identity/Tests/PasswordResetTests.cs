using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Email;
using Mersal.Identity.Api.Auth;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mersal.Identity.Domain;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.6 — self-service password reset (ADR-0036 §6).
///
/// <para>
/// Driven through the real host and the real ASP.NET Identity token machinery, because the properties that
/// matter are properties of that machinery: single-use comes from the SECURITY STAMP rotating, not from
/// anything written here, and a test that mocked the token provider would prove nothing about it.
/// </para>
/// </summary>
[Collection("identity-db")]
public class PasswordResetTests : IClassFixture<IdentityAppFactory>
{
    private readonly IdentityAppFactory _factory;
    public PasswordResetTests(IdentityAppFactory factory) => _factory = factory;

    private const string Pass = "Test-Passw0rd!";
    private const string NewPass = "Brand-New-Passw0rd!";

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> Csrf(HttpClient client) =>
        (await (await client.GetAsync("/connect/session/antiforgery"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body)
    {
        var csrf = await Csrf(client);
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-HBMP-CSRF", csrf);
        return await client.SendAsync(req);
    }

    /// <summary>Seeded identity that removes itself — the shared test DB rule from 28.3.</summary>
    private sealed class Seeded(IdentityAppFactory factory, Guid id, string name) : IAsyncDisposable
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public async ValueTask DisposeAsync() => await TestFlow.DeleteUser(factory, Id);
    }

    private async Task<Seeded> Seed(string prefix, bool twoFactor = false)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(_factory, name, Pass, ["reception"], twoFactor: twoFactor);
        return new Seeded(_factory, id, name);
    }

    /// <summary>Ask for a link and read the one a person would actually click, from the captured message.
    /// Deliberately NOT by calling GeneratePasswordResetTokenAsync directly — that would skip the encoding
    /// the link puts the token through, which is exactly where this shipped a defect.</summary>
    private async Task<(Guid UserId, string Token)> RequestLink(HttpClient client, string username)
    {
        CapturedEmail.Instance.Clear();
        (await Post(client, "/connect/password/forgot", new { username, lang = "en" }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        var text = CapturedEmail.Instance.Sent.Should().ContainSingle().Subject.Text;
        var m = Regex.Match(text, @"reset-password\?u=([^&\s]+)&t=([^\s]+)");
        m.Success.Should().BeTrue("the message must contain a usable link");
        return (Guid.Parse(m.Groups[1].Value), m.Groups[2].Value);
    }

    // ---- asking ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_unknown_username_gets_the_SAME_answer_as_a_real_one()
    {
        // Anything else makes this a free account-existence oracle needing no credentials at all — strictly
        // worse than the login form, which at least burns an attempt against a lockout counter.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-enum");
        var client = NewClient();

        var real = await Post(client, "/connect/password/forgot", new { username = user.Name, lang = "en" });
        var fake = await Post(client, "/connect/password/forgot", new { username = "no-such-person", lang = "en" });

        real.StatusCode.Should().Be(HttpStatusCode.Accepted);
        fake.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await real.Content.ReadAsStringAsync()).Should().Be(await fake.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Nothing_is_sent_for_an_account_that_does_not_exist()
    {
        // The reply says nothing; the SENDING must also say nothing, or the timing and the mail queue answer
        // the question the response refused to.
        Skip.If(IdentityTestDb.Conn is null);
        var client = NewClient();
        CapturedEmail.Instance.Clear();

        await Post(client, "/connect/password/forgot", new { username = "definitely-nobody", lang = "en" });

        CapturedEmail.Instance.Sent.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task The_message_says_the_link_is_short_lived_single_use_and_ignorable()
    {
        // The last of the three is what makes an unexpected reset email a warning rather than a scare: if you
        // did not ask for this, nothing has happened yet.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-copy");
        var client = NewClient();
        await RequestLink(client, user.Name);

        var text = CapturedEmail.Instance.Sent[0].Text;
        text.Should().Contain("30 minutes");
        text.Should().Contain("once");
        text.Should().Contain("didn't ask");
        text.Should().Contain("two-step verification", "a reset does not solve a lost authenticator");
    }

    // ---- using -------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_link_from_the_MESSAGE_works_end_to_end()
    {
        // THE regression test, and it is written this way for a reason. The link carries the token base64url
        // encoded because a raw Identity token does not survive a query string; the endpoint forgot to decode
        // it, so every correctly generated, correctly delivered, promptly clicked link came back "no longer
        // valid" — indistinguishable from an expired one. A test that posted a token it had never put through
        // a URL passed happily.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-roundtrip");
        var client = NewClient();
        var (id, token) = await RequestLink(client, user.Name);

        var res = await Post(client, "/connect/password/reset",
            new { userId = id, token, newPassword = NewPass });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task The_new_password_works_and_the_old_one_does_not()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-swap");
        var client = NewClient();
        var (id, token) = await RequestLink(client, user.Name);
        await Post(client, "/connect/password/reset", new { userId = id, token, newPassword = NewPass });

        (await SignInStatus(user.Name, NewPass)).Should().Be(SessionStatus.Authenticated);
        (await SignInStatus(user.Name, Pass)).Should().Be(SessionStatus.InvalidCredentials);
    }

    [SkippableFact]
    public async Task A_link_can_be_used_ONCE()
    {
        // Single-use is not enforced by anything written here — ResetPasswordAsync rotates the security stamp
        // the token is bound to, so every token issued before it stops verifying. That is the whole reason
        // there is no token table, and it is worth an assertion rather than a comment.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-once");
        var client = NewClient();
        var (id, token) = await RequestLink(client, user.Name);

        (await Post(client, "/connect/password/reset", new { userId = id, token, newPassword = NewPass }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post(client, "/connect/password/reset", new { userId = id, token, newPassword = "Third-Passw0rd!" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task An_OUTSTANDING_second_link_also_dies_when_the_first_is_used()
    {
        // Two requests in a row is ordinary user behaviour ("did that send?"). Both tokens are bound to the
        // same stamp, so using either kills both — which is the correct outcome and worth pinning, since a
        // bespoke token table would very likely have got it wrong.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-two");
        var client = NewClient();
        var (id, first) = await RequestLink(client, user.Name);
        var (_, second) = await RequestLink(client, user.Name);

        (await Post(client, "/connect/password/reset", new { userId = id, token = second, newPassword = NewPass }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post(client, "/connect/password/reset", new { userId = id, token = first, newPassword = "Other-Passw0rd!" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task A_password_that_fails_policy_says_WHY()
    {
        // Actionable, and revealing nothing about any account. Collapsing it into "that link is invalid"
        // would send somebody to request a new link over a password that was merely too short.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-policy");
        var client = NewClient();
        var (id, token) = await RequestLink(client, user.Name);

        var res = await Post(client, "/connect/password/reset", new { userId = id, token, newPassword = "short" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("12 characters");
    }

    [SkippableFact]
    public async Task A_garbage_token_is_an_invalid_link_and_not_a_crash()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-garbage");
        var client = NewClient();

        foreach (var token in new[] { "not-base64url-!!!", "", "YWJj" })
        {
            var res = await Post(client, "/connect/password/reset",
                new { userId = user.Id, token, newPassword = NewPass });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    // ---- the two consequences ----------------------------------------------------------------------

    [SkippableFact]
    public async Task Every_session_ends()
    {
        // If the reset was requested BECAUSE the account was compromised, leaving the attacker's live session
        // running defeats the entire exercise. The stamp rotation kills outstanding TOKENS; this is what
        // reaches the sessions, which the token endpoint checks by IsActive and never by security stamp.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-sessions");
        var client = NewClient();

        using (var scope = _factory.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            await sessions.OpenAsync(user.Id, null, "agent", null);
            await sessions.OpenAsync(user.Id, null, "agent", null);
        }
        (await LiveSessions(user.Id)).Should().Be(2);

        var (id, token) = await RequestLink(client, user.Name);
        await Post(client, "/connect/password/reset", new { userId = id, token, newPassword = NewPass });

        (await LiveSessions(user.Id)).Should().Be(0);
    }

    [SkippableFact]
    public async Task The_SECOND_FACTOR_is_not_touched()
    {
        // THE security property. If a reset could clear MFA, control of a mailbox would be a complete
        // account-takeover primitive and the second factor would be decorative on exactly the accounts worth
        // attacking. Asserted on the store AND on the behaviour, because either alone could pass while the
        // other was broken.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-2fa", twoFactor: true);
        var client = NewClient();
        var (id, token) = await RequestLink(client, user.Name);

        (await Post(client, "/connect/password/reset", new { userId = id, token, newPassword = NewPass }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var reloaded = await users.FindByIdAsync(user.Id.ToString());
            (await users.GetTwoFactorEnabledAsync(reloaded!)).Should().BeTrue();
            (await users.GetAuthenticatorKeyAsync(reloaded!)).Should().NotBeNullOrEmpty();
        }

        // And signing in with the NEW password still demands the second factor.
        (await SignInStatus(user.Name, NewPass)).Should().Be(SessionStatus.TwoFactorRequired);
    }

    [SkippableFact]
    public async Task A_reset_does_not_sign_anybody_in()
    {
        // A reset link sits in a mailbox. Turning one into a session would make mailbox access equal to
        // account access without even a password being typed.
        Skip.If(IdentityTestDb.Conn is null);
        await using var user = await Seed("reset-nosession");
        var client = NewClient();
        var (id, token) = await RequestLink(client, user.Name);

        var res = await Post(client, "/connect/password/reset", new { userId = id, token, newPassword = NewPass });

        res.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(c => c.Contains("mersal.idp=", StringComparison.Ordinal));
    }

    // ---- no transport ------------------------------------------------------------------------------

    [SkippableFact]
    public async Task With_no_email_transport_the_endpoint_REFUSES_rather_than_reporting_a_send()
    {
        // "If that account exists, we've sent you a link" while nothing was sent is a failed operation
        // rendered as a clean result, on the one screen a locked-out person reaches when nothing else works.
        // A capability that cannot work is absent, not broken and pretending.
        Skip.If(IdentityTestDb.Conn is null);
        await using var host = new NoEmailHost();
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await Post(client, "/connect/password/forgot", new { username = "anyone", lang = "en" });

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class NoEmailHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Identity"] = IdentityTestDb.Conn,
                ["Issuer:SeedDemoUsers"] = "false",
                ["Issuer:ServiceClientSecret"] = IdentityAppFactory.ServiceSecret,
                ["RateLimits:CredentialPerMinute"] = "10000",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new UnconfiguredEmailSender());
            });
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task<string> SignInStatus(string username, string password)
    {
        var client = NewClient();
        var res = await Post(client, "/connect/session", new { username, password });
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
    }

    private async Task<int> LiveSessions(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        return await db.Sessions.AsNoTracking().CountAsync(s => s.UserId == userId && s.RevokedAt == null);
    }
}
