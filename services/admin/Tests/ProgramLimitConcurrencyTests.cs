using FluentAssertions;
using Mersal.Admin.Infrastructure;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Tests;

/// <summary>
/// 21.4 — caps are counted LIVE inside the mutating transaction (design 40 §4).
///
/// The interesting case is concurrency, and it is the one a single-threaded test can never see. Two parallel
/// creates at cap−1 each run `SELECT count(*)`, each see the same pre-commit count under READ COMMITTED, and
/// both insert — so the tenant lands one over its cap and the limit silently did nothing. That is the defect
/// this file exists to catch, so the assertion is on the FINAL ROW COUNT, not merely on the responses.
///
/// Env-gated on ADMIN_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("admin-db")]
public class ProgramLimitConcurrencyTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");

    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    /// <summary>The counted resource. Any tenant-scoped admin table serves — what is under test is the
    /// count-and-insert race, not the semantics of this particular row.</summary>
    private static Task<int> CountAsync(AdminDbContext db, string tenant, CancellationToken ct) =>
        db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM admin.user_branch_assignment WHERE tenant_id = {0}", tenant)
            .SingleAsync(ct);

    private static Task InsertAsync(AdminDbContext db, string tenant, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO admin.user_branch_assignment
                (assignment_id, tenant_id, subject_user_id, branch_id, assignment_type, valid_from, status)
            VALUES ({Guid.NewGuid()}, {tenant}, {"u-" + Guid.NewGuid().ToString("N")[..8]}, {Guid.NewGuid()},
                    {"Additional"}, {new DateOnly(2026, 1, 1)}, {"Active"})
            """, ct);

    private static async Task SetLimitAsync(AdminDbContext db, string tenant, int max) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO admin.tenant_limit (tenant_id, limit_key, max_value)
            VALUES ({tenant}, {ProgramLimits.ActiveUsers}, {max})
            ON CONFLICT (tenant_id, limit_key) DO UPDATE SET max_value = EXCLUDED.max_value
            """);

    [SkippableFact]
    public async Task Two_parallel_creates_at_the_last_free_slot_yield_exactly_one_success()
    {
        Skip.If(Db is null, "test DB not configured — set ADMIN_TEST_DB to run this DB integration test.");
        var tenant = $"p24-{Guid.NewGuid():N}"[..16];

        try
        {
            await using (var setup = Ctx())
            {
                await SetLimitAsync(setup, tenant, max: 1);
            }

            // Both attempts start from a count of ZERO with a cap of ONE — the last free slot. The handshake
            // is fully async: an earlier version used Barrier.SignalAndWait, which blocks a thread-pool
            // thread inside an async test and deadlocked the run rather than failing it.
            var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var a = Attempt(tenant, startedA, release.Task);
            var b = Attempt(tenant, startedB, release.Task);

            // Only release once BOTH transactions are genuinely open, so they really overlap instead of
            // merely being started near each other — the sequential case passes trivially.
            await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(TimeSpan.FromSeconds(30));
            release.SetResult();

            var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(60));

            results.Count(r => r).Should().Be(1, "exactly one of two parallel creates may take the last slot");

            await using var check = Ctx();
            (await CountAsync(check, tenant, default)).Should().Be(1,
                "the FINAL row count is what matters — a cap that returns one refusal but still writes both " +
                "rows has not enforced anything");
        }
        finally { await CleanAsync(tenant); }
    }

    [SkippableFact]
    public async Task Freeing_a_row_frees_the_slot_immediately()
    {
        Skip.If(Db is null, "test DB not configured — set ADMIN_TEST_DB to run this DB integration test.");
        var tenant = $"p24f-{Guid.NewGuid():N}"[..16];

        try
        {
            await using var db = Ctx();
            await SetLimitAsync(db, tenant, max: 1);

            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                (await new TenantProgramStore(db).CheckLimitAsync(
                    tenant, ProgramLimits.ActiveUsers, ct => CountAsync(db, tenant, ct))).Should().BeNull();
                await InsertAsync(db, tenant, default);
                await tx.CommitAsync();
            }

            // At the cap.
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                (await new TenantProgramStore(db).CheckLimitAsync(
                    tenant, ProgramLimits.ActiveUsers, ct => CountAsync(db, tenant, ct))).Should().NotBeNull();
                await tx.RollbackAsync();
            }

            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM admin.user_branch_assignment WHERE tenant_id = {0}", tenant);

            // Free again, with nothing to decrement. This is what live counting buys: with a stored counter
            // it would be true only if every deletion path remembered, and the one that forgot would be
            // invisible until a tenant could not create a user it was entitled to.
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                (await new TenantProgramStore(db).CheckLimitAsync(
                    tenant, ProgramLimits.ActiveUsers, ct => CountAsync(db, tenant, ct))).Should().BeNull();
                await tx.RollbackAsync();
            }
        }
        finally { await CleanAsync(tenant); }
    }

    [SkippableFact]
    public async Task An_unconfigured_cap_never_refuses()
    {
        Skip.If(Db is null, "test DB not configured — set ADMIN_TEST_DB to run this DB integration test.");
        var tenant = $"p24u-{Guid.NewGuid():N}"[..16];

        try
        {
            await using var db = Ctx();
            for (var i = 0; i < 3; i++) await InsertAsync(db, tenant, default);

            // No tenant_limit row at all. Inventing a default would take a working platform offline for
            // every tenant nobody had configured, on the day this shipped.
            (await new TenantProgramStore(db).CheckLimitAsync(
                tenant, ProgramLimits.ActiveUsers, ct => CountAsync(db, tenant, ct))).Should().BeNull();
        }
        finally { await CleanAsync(tenant); }
    }

    /// <summary>One create attempt in its own connection + transaction, released together with the others so
    /// they genuinely overlap rather than merely being started near each other.</summary>
    private static async Task<bool> Attempt(string tenant, TaskCompletionSource started, Task release)
    {
        await using var db = Ctx();
        await using var tx = await db.Database.BeginTransactionAsync();

        // A lock wait must fail the test, never hang the suite. Without this, a regression in the advisory
        // lock would time out the whole CI job with no indication of which test was stuck.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '30s'");

        started.SetResult();
        await release;

        var refusal = await new TenantProgramStore(db).CheckLimitAsync(
            tenant, ProgramLimits.ActiveUsers, ct => CountAsync(db, tenant, ct));
        if (refusal is not null)
        {
            await tx.RollbackAsync();
            return false;
        }

        await InsertAsync(db, tenant, default);
        await tx.CommitAsync();
        return true;
    }

    private static async Task CleanAsync(string tenant)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM admin.user_branch_assignment WHERE tenant_id = {0}", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM admin.tenant_limit WHERE tenant_id = {0}", tenant);
    }
}
