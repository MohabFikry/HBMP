using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.11 — the pruning pass that keeps the OpenIddict token table from growing without bound.
///
/// <para>
/// ============================================================================================================
/// WHAT THESE PROVE
/// ============================================================================================================
/// The framework's own predicate decides what is prunable, so these are not testing OpenIddict. They are
/// testing the two claims THIS service makes on top of it, either of which being wrong would be expensive:
/// </para>
///
/// <list type="bullet">
///   <item>a spent token older than the window is actually removed — otherwise the job is decorative, which is
///         indistinguishable from the situation it was written to fix;</item>
///   <item>a token still backing a session is NOT removed, however old the row is. That is the one that
///         matters. A pruner that took a live refresh token with it would sign people out at an interval
///         nobody could correlate with anything, and the symptom would be "the platform logs me out
///         randomly" rather than anything pointing at a maintenance job.</item>
/// </list>
/// </summary>
[Collection("identity-db")]
public class TokenPruningTests(IdentityHostFixture host) : IClassFixture<IdentityHostFixture>
{
    private const string Pass = "Test-Passw0rd!";

    [SkippableFact]
    public async Task A_spent_token_older_than_the_window_is_removed()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"prune-spent-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            // A real sign-in, so the rows are the ones the issuer actually writes rather than a hand-built
            // approximation of them.
            await TestFlow.AuthCodeToken(host.Factory, name, Pass, null, "openid offline_access");
            var subject = id.ToString();
            (await TokenCount(subject)).Should().BeGreaterThan(0, "signing in mints tokens");

            // Age them past the window AND spend them. Both halves are needed: OpenIddict prunes what is old
            // and finished, never what is merely old.
            await Backdate(subject, ageDays: 60, expired: true, status: "redeemed");

            await Prune(TimeSpan.FromDays(30));

            (await TokenCount(subject)).Should().Be(0);
        }
        finally { await TestFlow.DeleteUser(host.Factory, id); }
    }

    /// <summary>
    /// THE ONE THAT MUST NEVER GO RED.
    ///
    /// <para>A pruner that misses a dead row wastes a few kilobytes until tomorrow. A pruner that takes a live
    /// one signs somebody out mid-shift, on a schedule nothing on their screen explains.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_token_still_backing_a_session_survives_however_old_the_row_is()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"prune-live-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            await TestFlow.AuthCodeToken(host.Factory, name, Pass, null, "openid offline_access");
            var subject = id.ToString();

            // Old enough to be well past any threshold, and still valid — a long-lived session, which is
            // exactly the shape a naive `WHERE creation_date < threshold` would destroy.
            await Backdate(subject, ageDays: 400, expired: false, status: "valid");
            var before = await TokenCount(subject);
            before.Should().BeGreaterThan(0);

            await Prune(TimeSpan.FromDays(30));

            (await TokenCount(subject)).Should().Be(before, "age alone is not a reason to revoke a credential");
        }
        finally { await TestFlow.DeleteUser(host.Factory, id); }
    }

    [SkippableFact]
    public async Task A_spent_token_inside_the_window_is_left_for_the_forensic_period()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"prune-recent-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            await TestFlow.AuthCodeToken(host.Factory, name, Pass, null, "openid offline_access");
            var subject = id.ToString();

            // Finished, but only a day old. The window is the point of the setting: "what was issued to this
            // account last week" stays answerable from this table for as long as the retention says.
            await Backdate(subject, ageDays: 1, expired: true, status: "redeemed");
            var before = await TokenCount(subject);

            await Prune(TimeSpan.FromDays(30));

            (await TokenCount(subject)).Should().Be(before);
        }
        finally { await TestFlow.DeleteUser(host.Factory, id); }
    }

    // ---- harness ---------------------------------------------------------------------------------------

    /// <summary>Run one pruning pass at the given window, through the same managers the job uses.</summary>
    private async Task Prune(TimeSpan retention)
    {
        using var scope = host.Factory.Services.CreateScope();
        var threshold = DateTimeOffset.UtcNow - retention;
        await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(threshold);
        await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(threshold);
    }

    private static async Task<int> TokenCount(string subject)
    {
        await using var db = IdentityTestDb.NewContext();
        return await db.Database
            .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM identity."OpenIddictTokens" WHERE subject = {subject}""")
            .SingleAsync();
    }

    /// <summary>
    /// Move one account's tokens into the past, and set how finished they are.
    ///
    /// <para>Written straight to the table rather than through the manager: there is no supported way to
    /// author a token that is already old, and waiting sixty days for the fixture is not an option. The
    /// COLUMNS are what the pruning predicate reads, so this is setting up the exact state a real
    /// sixty-day-old token would be in.</para>
    /// </summary>
    private static async Task Backdate(string subject, int ageDays, bool expired, string status)
    {
        await using var db = IdentityTestDb.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE identity."OpenIddictTokens"
               SET creation_date   = now() - make_interval(days => {1}),
                   expiration_date = CASE WHEN {2} THEN now() - interval '1 hour'
                                          ELSE now() + interval '365 days' END,
                   status          = {3}
             WHERE subject = {0}
            """, subject, ageDays, expired, status);
    }
}
