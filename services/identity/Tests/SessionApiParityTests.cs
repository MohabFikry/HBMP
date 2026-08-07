using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Identity.Api.Auth;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.3 — the API sign-in path must do everything the FORM sign-in path does (ADR-0036 §5.3).
///
/// <para>
/// ============================================================================================================
/// WHY THESE COMPARE THE TWO PATHS INSTEAD OF CHECKING ONE
/// ============================================================================================================
/// <c>SessionService.RecordAttemptAsync</c> and <c>OpenAsync</c> are called from inside the form handlers.
/// Moving the login to an API means writing those calls again, and forgetting one does not fail loudly — it
/// silently empties <c>/identity/me/login-history</c>, which exists so that a person can see their own
/// account being attacked, and silently stops enforcing the concurrent-session cap.
/// </para>
/// <para>
/// A test that asserted "the API writes a login attempt" would pass a version that wrote the wrong reason, or
/// dropped failures, or recorded the gateway's address for every user. So each of these signs in BOTH ways
/// and asserts the two produced the same record. The form path is the specification; the API has to match it.
/// </para>
/// </summary>
[Collection("identity-db")]
public class SessionApiParityTests : IClassFixture<IdentityAppFactory>
{
    private readonly IdentityAppFactory _factory;
    public SessionApiParityTests(IdentityAppFactory factory) => _factory = factory;

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


    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> Csrf(HttpClient client) =>
        (await (await client.GetAsync("/connect/session/antiforgery"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

    private async Task ApiSignIn(string username, string password)
    {
        var client = NewClient();
        var csrf = await Csrf(client);
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/session")
        { Content = JsonContent.Create(new { username, password }) };
        req.Headers.Add("X-HBMP-CSRF", csrf);
        await client.SendAsync(req);
    }

    private async Task FormSignIn(string username, string password)
    {
        var client = NewClient();
        var fields = await TestFlow.AntiforgeryFields(client, "/connect/login");
        await client.PostAsync("/connect/login", new FormUrlEncodedContent(
            new Dictionary<string, string>(fields) { ["username"] = username, ["password"] = password }));
    }

    private async Task<IReadOnlyList<LoginAttempt>> Attempts(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        return await db.LoginAttempts.AsNoTracking()
            .Where(a => a.UserId == userId).OrderBy(a => a.AttemptedAt).ToListAsync();
    }

    private async Task<int> LiveSessions(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        return await db.Sessions.AsNoTracking().CountAsync(s => s.UserId == userId && s.RevokedAt == null);
    }

    // ---- the history --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_successful_sign_in_is_recorded_identically_by_both_paths()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("parity-ok");
        var (name, userId) = (seeded.Name, seeded.Id);

        await FormSignIn(name, Pass);
        await ApiSignIn(name, Pass);

        var rows = await Attempts(userId);
        rows.Should().HaveCount(2, "both paths record a sign-in");
        rows.Select(r => r.Succeeded).Should().AllBeEquivalentTo(true);
        rows.Select(r => r.FailureReason).Should().AllSatisfy(r => r.Should().BeNull());
        rows.Select(r => r.UsernameTried).Should().AllBeEquivalentTo(name);
    }

    [SkippableFact]
    public async Task A_wrong_password_is_recorded_by_both_paths_with_the_same_reason()
    {
        // The failures are the half that matters. A history containing only the successes cannot show anyone
        // that their account is being attacked — which is the entire purpose of the screen.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("parity-bad");
        var (name, userId) = (seeded.Name, seeded.Id);

        await FormSignIn(name, "Wrong-Passw0rd!");
        await ApiSignIn(name, "Wrong-Passw0rd!");

        var rows = await Attempts(userId);
        rows.Should().HaveCount(2);
        rows.Select(r => r.Succeeded).Should().AllBeEquivalentTo(false);
        rows.Select(r => r.FailureReason).Distinct().Should()
            .ContainSingle().Which.Should().Be(LoginFailureReasons.BadCredentials,
                "one coarse reason, so the distinction cannot leak into a support screen");
    }

    [SkippableFact]
    public async Task A_deactivated_account_records_the_same_reason_on_both_paths()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("parity-inactive");
        var (name, userId) = (seeded.Name, seeded.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
            var u = await db.Users.FindAsync(userId);
            u!.IsActive = false;
            await db.SaveChangesAsync();
        }

        await FormSignIn(name, Pass);
        await ApiSignIn(name, Pass);

        var rows = await Attempts(userId);
        rows.Should().HaveCount(2);
        rows.Select(r => r.FailureReason).Distinct().Should()
            .ContainSingle().Which.Should().Be(LoginFailureReasons.Inactive);
    }

    [SkippableFact]
    public async Task Neither_path_records_an_outcome_for_a_password_step_that_only_asks_for_a_second_factor()
    {
        // Recording here would file a "failure" against every successful MFA sign-in and make the history
        // least readable for exactly the accounts that are best protected. Both paths must agree to stay
        // quiet, and this is the one place where doing LESS is the correct behaviour.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("parity-2fa", twoFactor: true);
        var (name, userId) = (seeded.Name, seeded.Id);

        await FormSignIn(name, Pass);
        await ApiSignIn(name, Pass);

        (await Attempts(userId)).Should().BeEmpty();
    }

    // ---- the concurrent-session cap ------------------------------------------------------------------

    [SkippableFact]
    public async Task Both_paths_open_a_session_so_the_concurrent_cap_keeps_applying()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("parity-session");
        var (name, userId) = (seeded.Name, seeded.Id);

        await FormSignIn(name, Pass);
        var afterForm = await LiveSessions(userId);
        await ApiSignIn(name, Pass);
        var afterApi = await LiveSessions(userId);

        afterForm.Should().Be(1);
        afterApi.Should().Be(2, "the API path must open a session too, or the cap silently stops applying");
    }

    [SkippableFact]
    public async Task The_cap_is_enforced_when_the_API_is_the_path_that_exceeds_it()
    {
        // Signing in repeatedly through the API must revoke the oldest, exactly as the form does. If OpenAsync
        // were missing, this would sit at whatever the form path last left and look perfectly healthy.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("parity-cap");
        var (name, userId) = (seeded.Name, seeded.Id);

        for (var i = 0; i < SessionService.ConcurrentSessionCap + 3; i++) await ApiSignIn(name, Pass);

        (await LiveSessions(userId)).Should().Be(SessionService.ConcurrentSessionCap);
    }

    // ---- the recorded address ------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_address_recorded_is_the_CLIENT_not_the_gateway()
    {
        // 28.1's lesson applied to the history rather than to the rate limiter. Recording the gateway would
        // make the screen that shows a person WHERE their account is being used show them one constant, on
        // every row, forever — an answer that looks like data.
        Skip.If(IdentityTestDb.Conn is null);
        await using var seeded = await Seed("parity-ip");
        var (name, userId) = (seeded.Name, seeded.Id);

        var client = NewClient();
        var csrf = await Csrf(client);
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/session")
        { Content = JsonContent.Create(new { username = name, password = Pass }) };
        req.Headers.Add("X-HBMP-CSRF", csrf);
        req.Headers.Add("X-Forwarded-For", "203.0.113.77");
        await client.SendAsync(req);

        var rows = await Attempts(userId);
        rows.Should().ContainSingle();
        rows[0].IpAddress?.ToString().Should().Be("203.0.113.77");
    }

    // ---- the limit that had to become configurable ---------------------------------------------------

    [Fact]
    public void The_shipped_credential_limit_is_still_ten_a_minute()
    {
        // 28.3 made the limit configurable so the in-process test host could stop throttling itself. That is
        // only safe while the DEFAULT is the safe one, so the default is pinned here rather than left to
        // whatever the last person to edit a config file typed.
        IssuerRateLimits.DefaultCredentialPerMinute.Should().Be(10);
        IssuerRateLimits.DefaultTokenPerMinute.Should().Be(60);
    }

    // ---- the status vocabulary is closed -------------------------------------------------------------

    [SkippableFact]
    public async Task Every_reply_uses_a_status_from_the_declared_set()
    {
        // A status the SPA has never heard of renders as nothing at all — a login form that submits and then
        // sits there. Pinned so a new branch has to add its status here first.
        Skip.If(IdentityTestDb.Conn is null);
        var known = new[]
        {
            SessionStatus.Authenticated, SessionStatus.TwoFactorRequired,
            SessionStatus.MembershipSelectionRequired, SessionStatus.NoMembership,
            SessionStatus.Locked, SessionStatus.InvalidCredentials,
        };

        await using var seeded = await Seed("parity-vocab");
        var name = seeded.Name;

        foreach (var (user, pass) in new[] { (name, Pass), (name, "Wrong-1!"), ("nobody-here", "Wrong-1!") })
        {
            var client = NewClient();
            var csrf = await Csrf(client);
            var req = new HttpRequestMessage(HttpMethod.Post, "/connect/session")
            { Content = JsonContent.Create(new { username = user, password = pass }) };
            req.Headers.Add("X-HBMP-CSRF", csrf);
            var res = await client.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var status = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString();
            known.Should().Contain(status);
        }
    }
}
