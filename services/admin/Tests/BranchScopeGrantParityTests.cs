using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mersal.Admin.Infrastructure;

namespace Mersal.Admin.Tests;

/// <summary>
/// 21.3 — the copy from <c>user_branch_assignment</c> into <c>branch_scope_grant</c> (migration 0007).
///
/// ROW PARITY is the acceptance criterion: nobody gains or loses a branch by migration alone. This replays
/// the REAL migration's copy block against seeded assignments and compares the two tables, so a copy that
/// silently drops revoked rows, flips the home flag, or loses a validity window fails here rather than in
/// production as an unexplained change to somebody's reach.
///
/// Env-gated on ADMIN_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("admin-db")]
public class BranchScopeGrantParityTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");

    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    /// <summary>The copy statements out of the real migration file — not a paraphrase. A hand-written copy of
    /// the SQL here would pass forever while the migration that actually runs drifted away from it.</summary>
    private static string CopyBlock()
    {
        var path = Path.Combine(RepoRoot(), "services", "admin", "Infrastructure", "Migrations",
            "0007_branch_scope_grant.sql");
        var sql = File.ReadAllText(path);
        var inserts = Regex.Matches(sql, @"INSERT INTO admin\.branch_scope_grant.*?;", RegexOptions.Singleline)
            .Select(m => m.Value).ToArray();
        inserts.Should().HaveCount(2, "0007 must still contain the grant copy and its history twin");
        return string.Join("\n", inserts);
    }

    [SkippableFact]
    public async Task Every_assignment_is_copied_with_its_window_home_flag_and_revocation_intact()
    {
        Skip.If(Db is null, "test DB not configured — set ADMIN_TEST_DB to run this DB integration test.");
        var tenant = $"p23-{Guid.NewGuid():N}"[..16];
        var subject = $"u-{Guid.NewGuid():N}"[..12];
        await using var db = Ctx();

        try
        {
            // A shape that exercises every column the copy has to preserve: a home, an open-ended
            // additional, an expiring additional, and a revoked one.
            var rows = new (Guid Id, Guid Branch, string Type, DateOnly From, DateOnly? To, string Status)[]
            {
                (Guid.NewGuid(), Guid.NewGuid(), "Home",       new DateOnly(2026, 1, 1), null, "Active"),
                (Guid.NewGuid(), Guid.NewGuid(), "Additional", new DateOnly(2026, 2, 1), null, "Active"),
                (Guid.NewGuid(), Guid.NewGuid(), "Additional", new DateOnly(2026, 3, 1), new DateOnly(2026, 10, 31), "Active"),
                (Guid.NewGuid(), Guid.NewGuid(), "Additional", new DateOnly(2026, 4, 1), null, "Revoked"),
            };

            foreach (var r in rows)
            {
                // Interpolated, not Raw: a nullable DateOnly passed through the object[] overload arrives as
                // an untyped null that Npgsql cannot map.
                var to = r.To;
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO admin.user_branch_assignment
                        (assignment_id, tenant_id, subject_user_id, branch_id, assignment_type, valid_from, valid_to, status)
                    VALUES ({r.Id}, {tenant}, {subject}, {r.Branch}, {r.Type}, {r.From}, {to}, {r.Status})
                    """);
            }

            await db.Database.ExecuteSqlRawAsync(CopyBlock());

            var copied = await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM admin.branch_scope_grant WHERE tenant_id = {0}", tenant)
                .SingleAsync();
            copied.Should().Be(rows.Length, "every assignment must be copied, including the revoked one — " +
                "skipping it would leave the two tables impossible to reconcile row for row");

            // Home flag, window and revocation, each checked against the source rather than assumed.
            var mismatches = await db.Database.SqlQueryRaw<int>(
                """
                SELECT count(*)::int AS "Value"
                FROM admin.user_branch_assignment a
                JOIN admin.branch_scope_grant g ON g.grant_id = a.assignment_id
                WHERE a.tenant_id = {0}
                  AND (   g.branch_id   IS DISTINCT FROM a.branch_id
                       OR g.is_home     IS DISTINCT FROM (a.assignment_type = 'Home')
                       OR g.valid_from  IS DISTINCT FROM a.valid_from
                       OR g.valid_until IS DISTINCT FROM a.valid_to
                       OR g.is_deleted  IS DISTINCT FROM (a.status = 'Revoked')
                       OR g.subject_user_id IS DISTINCT FROM a.subject_user_id)
                """, tenant).SingleAsync();
            mismatches.Should().Be(0, "no field may change value in the copy");

            // The ACTIVE set is what actually governs reach, so assert it directly rather than trusting the
            // per-column comparison to imply it.
            var activeGrants = await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM admin.branch_scope_grant WHERE tenant_id = {0} AND NOT is_deleted",
                tenant).SingleAsync();
            activeGrants.Should().Be(3, "the three non-revoked assignments, and only those");

            // Re-running must not duplicate — migrations get replayed.
            await db.Database.ExecuteSqlRawAsync(CopyBlock());
            (await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM admin.branch_scope_grant WHERE tenant_id = {0}", tenant)
                .SingleAsync()).Should().Be(rows.Length, "the copy must be idempotent");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM admin.branch_scope_grant_history WHERE tenant_id = {0}", tenant);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM admin.branch_scope_grant WHERE tenant_id = {0}", tenant);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM admin.user_branch_assignment WHERE tenant_id = {0}", tenant);
        }
    }

    /// <summary>
    /// A home branch that has been RE-ISSUED since the last migration pass does not break the next one.
    /// </summary>
    /// <remarks>
    /// <para><b>The failure this exists for.</b> The copy guarded itself with
    /// <c>NOT EXISTS (grant_id = assignment_id)</c> — correct, and keyed on the wrong thing. The grant table
    /// has a second uniqueness rule, <c>ux_bsg_home_per_subject</c>, keyed on
    /// <c>(tenant_id, subject_user_id)</c>. The two disagree the moment somebody's home branch changes: the
    /// app revokes the old assignment and writes a new one with a fresh id, the id guard sees an uncopied
    /// assignment, and the insert collides with the home grant copied from its predecessor.</para>
    ///
    /// <para><b>Why it mattered more than one failing statement.</b> <c>apply-migrations.sh</c> runs under
    /// <c>set -e</c> and replays every file on every pass, so from the first re-issued home onward the script
    /// aborted here — and every service after <c>admin/</c> alphabetically silently stopped receiving
    /// migrations. It surfaced as orders- and pharmacy-service tests failing on missing tables, several
    /// migrations away from the cause, against a database that looked migrated. The previous idempotence
    /// assertion could not catch it: it replays the copy over an UNCHANGED source, which is the one case the
    /// id guard already handled.</para>
    /// </remarks>
    [SkippableFact]
    public async Task A_home_branch_reissued_between_passes_does_not_break_the_next_one()
    {
        Skip.If(Db is null, "test DB not configured — set ADMIN_TEST_DB to run this DB integration test.");
        var tenant = $"p23-{Guid.NewGuid():N}"[..16];
        var subject = $"u-{Guid.NewGuid():N}"[..12];
        await using var db = Ctx();

        async Task HomeAsync(Guid id, Guid branch, string status) =>
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO admin.user_branch_assignment
                    (assignment_id, tenant_id, subject_user_id, branch_id, assignment_type, valid_from, valid_to, status)
                VALUES ({id}, {tenant}, {subject}, {branch}, 'Home', {new DateOnly(2026, 1, 1)}, {(DateOnly?)null}, {status})
                """);

        try
        {
            // Pass one: the home as it stood, copied.
            var first = Guid.NewGuid();
            await HomeAsync(first, Guid.NewGuid(), "Active");
            await db.Database.ExecuteSqlRawAsync(CopyBlock());

            // Between passes the person moves branch. `ux_user_home_branch` allows only one ACTIVE home, so
            // the app revokes the old row before writing the new one — which is exactly what leaves a live
            // home grant whose id no longer matches any uncopied assignment.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE admin.user_branch_assignment SET status = 'Revoked' WHERE assignment_id = {first}");
            await HomeAsync(Guid.NewGuid(), Guid.NewGuid(), "Active");

            // Pass two. This threw 23505 before the home guard, aborting the whole migration script.
            var replay = async () => await db.Database.ExecuteSqlRawAsync(CopyBlock());
            await replay.Should().NotThrowAsync(
                "a re-issued home must not collide with the grant copied from its predecessor — the script "
                + "runs under set -e, so this one statement stops every migration after admin/");

            // And reach is unchanged, which is what row parity actually asserts: one live home, not two and
            // not none. A guard that suppressed too much would show up here as zero.
            var liveHomes = await db.Database.SqlQueryRaw<int>(
                """
                SELECT count(*)::int AS "Value" FROM admin.branch_scope_grant
                 WHERE tenant_id = {0} AND is_home AND NOT is_deleted
                """, tenant).SingleAsync();
            liveHomes.Should().Be(1, "the principal has exactly one home branch, before and after");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM admin.branch_scope_grant_history WHERE tenant_id = {0}", tenant);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM admin.branch_scope_grant WHERE tenant_id = {0}", tenant);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM admin.user_branch_assignment WHERE tenant_id = {0}", tenant);
        }
    }

    [SkippableFact]
    public async Task A_grant_with_neither_a_membership_nor_a_user_is_rejected_by_the_schema()
    {
        Skip.If(Db is null, "test DB not configured — set ADMIN_TEST_DB to run this DB integration test.");
        await using var db = Ctx();

        // An unattributed grant is reach nobody can review or revoke, so the database refuses to hold one.
        var act = async () => await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO admin.branch_scope_grant (grant_id, tenant_id, branch_id, valid_from)
            VALUES ({0}, {1}, {2}, {3})
            """, Guid.NewGuid(), $"p23x-{Guid.NewGuid():N}"[..16], Guid.NewGuid(), new DateOnly(2026, 1, 1));

        await act.Should().ThrowAsync<Exception>();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
