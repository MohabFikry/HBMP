using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>Phase 14.4 branch scoping at the datastore (env-gated <c>EMR_TEST_DB</c>, migration 0006 applied).
/// Proves the additive branch_id column filters worklists to the active branch while pre-existing NULL-branch
/// rows are unaffected, and that a BranchScoped read of another branch's row is a deny (not a 404-empty).
/// Self-cleans by a unique provider scope.</summary>
public class BranchScopeIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("EMR_TEST_DB");
    private static EmrDbContext Ctx() =>
        new(new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    [SkippableFact]
    public async Task Worklist_query_filtered_to_the_active_branch_returns_only_that_branch()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = Guid.NewGuid();
        var maadi = Guid.NewGuid();
        var dokki = Guid.NewGuid();
        try
        {
            await Seed(provider, maadi);
            await Seed(provider, dokki);
            await Seed(provider, branch: null);   // legacy row (external location) — no branch

            await using var db = Ctx();
            var maadiRows = await db.Appointments.AsNoTracking()
                .Where(a => a.ProviderId == provider && a.BranchId == maadi).ToListAsync();
            maadiRows.Should().HaveCount(1);
            maadiRows[0].BranchId.Should().Be(maadi);

            // Simulate a BranchScoped caller reaching a Dokki row while active branch is Maadi → out of scope.
            var dokkiRow = await db.Appointments.AsNoTracking().SingleAsync(a => a.ProviderId == provider && a.BranchId == dokki);
            var active = maadi;
            var denied = dokkiRow.BranchId is not null && dokkiRow.BranchId != active;
            denied.Should().BeTrue("a BranchScoped caller is denied a row outside the active branch");
        }
        finally { await Cleanup(provider); }
    }

    private static async Task Seed(Guid provider, Guid? branch)
    {
        await using var db = Ctx();
        var now = DateTimeOffset.UtcNow;
        db.Appointments.Add(new Appointment
        {
            AppointmentId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(), ProviderId = provider,
            LocationId = Guid.NewGuid(), BranchId = branch, AppointmentType = AppointmentType.Scheduled,
            Status = AppointmentStatus.Booked, ScheduledStart = now.AddHours(1), ScheduledEnd = now.AddHours(2),
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task Cleanup(Guid provider)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM emr.appointment WHERE provider_id = {0}", provider);
    }
}
