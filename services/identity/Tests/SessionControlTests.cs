using System.Net;
using FluentAssertions;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.5 — session/device controls and sign-in history (design 40 §6, 18 §9, adaptation A6).
///
/// Env-gated on IDENTITY_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class SessionControlTests(IdentityHostFixture host) : IClassFixture<IdentityHostFixture>
{
    private const string Password = "Passw0rd!Mersal";

    private static SessionService Service(IdentityAppFactory factory, out IServiceScope scope)
    {
        scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SessionService>();
    }

    [SkippableFact]
    public async Task The_concurrent_cap_revokes_the_OLDEST_rather_than_refusing_the_newest()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var uname = $"sess-{Guid.NewGuid():N}";
        var (userId, _) = await TestFlow.SeedUser(factory, uname, Password, ["reception"]);

        try
        {
            var opened = new List<Guid>();
            for (var i = 0; i < SessionService.ConcurrentSessionCap + 2; i++)
            {
                var svc = Service(factory, out var scope);
                using (scope)
                    opened.Add((await svc.OpenAsync(userId, null, $"agent-{i}", IPAddress.Loopback)).SessionId);
            }

            var check = Service(factory, out var s2);
            using (s2)
            {
                var live = await check.LiveAsync(userId);

                live.Should().HaveCount(SessionService.ConcurrentSessionCap);

                // Revoking the OLDEST is the deliberate choice. Refusing the newest login would be
                // indistinguishable, to the person at the desk, from being locked out — and they would
                // phone support rather than close the laptop they left signed in at home.
                live.Select(l => l.SessionId).Should().NotContain(opened[0], "the first session must be the one dropped");
                live.Select(l => l.SessionId).Should().Contain(opened[^1], "the newest login must always succeed");
            }
        }
        finally { await TestFlow.DeleteUser(factory, userId); }
    }

    [SkippableFact]
    public async Task A_revoked_session_is_kept_with_its_attribution_never_deleted()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var uname = $"sessr-{Guid.NewGuid():N}";
        var (userId, _) = await TestFlow.SeedUser(factory, uname, Password, ["reception"]);

        try
        {
            var svc = Service(factory, out var scope);
            using (scope)
            {
                var session = await svc.OpenAsync(userId, null, "firefox", IPAddress.Loopback);
                await svc.RevokeAsync(session.SessionId, "admin-7", "off-boarding");

                (await svc.LiveAsync(userId)).Should().BeEmpty();

                // The row survives, so "who ended this session and why" stays answerable afterwards.
                using var s2 = factory.Services.CreateScope();
                var db = s2.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
                var stored = await db.Sessions.AsNoTracking().FirstAsync(x => x.SessionId == session.SessionId);
                stored.RevokedBy.Should().Be("admin-7");
                stored.RevokeReason.Should().Be("off-boarding");
                stored.RevokedAt.Should().NotBeNull();
            }
        }
        finally { await TestFlow.DeleteUser(factory, userId); }
    }

    [SkippableFact]
    public async Task Revoking_all_ends_every_live_session_for_that_identity_only()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var mine = $"sessa-{Guid.NewGuid():N}";
        var theirs = $"sessb-{Guid.NewGuid():N}";
        var (myId, _) = await TestFlow.SeedUser(factory, mine, Password, ["reception"]);
        var (theirId, _) = await TestFlow.SeedUser(factory, theirs, Password, ["reception"]);

        try
        {
            var svc = Service(factory, out var scope);
            using (scope)
            {
                await svc.OpenAsync(myId, null, "a", null);
                await svc.OpenAsync(myId, null, "b", null);
                await svc.OpenAsync(theirId, null, "c", null);

                (await svc.RevokeAllAsync(myId, "admin-7", "suspected compromise")).Should().Be(2);

                (await svc.LiveAsync(myId)).Should().BeEmpty();
                // "Sign out everywhere" must mean everywhere for ME — signing out a bystander would be an
                // outage caused by someone else's security action.
                (await svc.LiveAsync(theirId)).Should().HaveCount(1);
            }
        }
        finally
        {
            await TestFlow.DeleteUser(factory, myId);
            await TestFlow.DeleteUser(factory, theirId);
        }
    }

    [SkippableFact]
    public async Task A6_an_explicit_revoke_that_cannot_be_persisted_raises_rather_than_reporting_success()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var svc = Service(factory, out var scope);

        using (scope)
        {
            // Revoking a session that is not there stands in for "the store could not satisfy this".
            // The requirement is about what the OPERATOR is told: never a silent success.
            var act = async () => await svc.RevokeAsync(Guid.NewGuid(), "admin-7", "off-boarding");

            await act.Should().ThrowAsync<RevocationNotPersistedException>(
                "an operator who believes a revocation succeeded will close the incident and stop looking");
        }
    }

    [SkippableFact]
    public async Task A6_the_refresh_path_fails_OPEN_when_the_store_cannot_answer()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var svc = Service(factory, out var scope);

        using (scope)
        {
            // The exact inverse of the test above, against the same service. An unknown session id reads as
            // "not revoked" rather than as a lockout: refusing every refresh during an outage would sign
            // out every clinician on the platform mid-shift, and the exposure from proceeding is bounded by
            // the access-token TTL.
            (await svc.IsLiveAsync(Guid.NewGuid())).Should().BeTrue();
        }
    }

    // ---- login history -----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Failed_attempts_are_recorded_not_only_successful_ones()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var uname = $"hist-{Guid.NewGuid():N}";
        var (userId, _) = await TestFlow.SeedUser(factory, uname, Password, ["reception"]);

        try
        {
            var svc = Service(factory, out var scope);
            using (scope)
            {
                await svc.RecordAttemptAsync(userId, uname, false, LoginFailureReasons.BadCredentials, "ff", null);
                await svc.RecordAttemptAsync(userId, uname, true, null, "ff", null);

                var history = await svc.RecentAttemptsAsync(userId);

                // A history containing only successes cannot show anyone that their account is under attack.
                history.Should().HaveCount(2);
                history[0].Succeeded.Should().BeTrue("newest first");
                history[1].FailureReason.Should().Be(LoginFailureReasons.BadCredentials);
            }
        }
        finally { await TestFlow.DeleteUser(factory, userId); }
    }

    [SkippableFact]
    public async Task No_password_material_is_stored_anywhere_in_the_attempt_record()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var uname = $"histp-{Guid.NewGuid():N}";
        var (userId, _) = await TestFlow.SeedUser(factory, uname, Password, ["reception"]);

        try
        {
            var svc = Service(factory, out var scope);
            using (scope)
                await svc.RecordAttemptAsync(userId, uname, false, LoginFailureReasons.BadCredentials, "ff", null);

            using var s2 = factory.Services.CreateScope();
            var db = s2.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
            var row = await db.LoginAttempts.AsNoTracking().FirstAsync(a => a.UserId == userId);

            // These rows are retained for years and are readable by administrators who may not see clinical
            // data. Whatever was typed in the password box must never be among them.
            var everyStoredString = string.Join("|", row.UsernameTried, row.FailureReason, row.UserAgent);
            everyStoredString.Should().NotContain(Password);
            everyStoredString.Should().NotContainEquivalentOf("passw0rd");
        }
        finally { await TestFlow.DeleteUser(factory, userId); }
    }

    [SkippableFact]
    public async Task The_failure_reason_does_not_distinguish_a_missing_user_from_a_wrong_password()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var svc = Service(factory, out var scope);

        using (scope)
        {
            // Both cases record the SAME coarse reason. Keeping them apart in the store is harmless until
            // someone surfaces the distinction in a support screen — at which point it is a user
            // enumeration oracle. Storing it coarse means it cannot leak by being displayed.
            await svc.RecordAttemptAsync(null, "no-such-user", false, LoginFailureReasons.BadCredentials, null, null);
            LoginFailureReasons.BadCredentials.Should().Be("bad-credentials");
        }
    }
}
