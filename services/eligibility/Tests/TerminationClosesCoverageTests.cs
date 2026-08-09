using System.Text.Json;
using FluentAssertions;
using Mersal.Eligibility.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Tests;

/// <summary>
/// The consumer half of "a terminated member must stop being covered".
///
/// <para>policy-service ends a membership by end-dating its coverage rows and publishing
/// <c>MemberTerminated</c>. This service's projection switch has no case for that event — it falls through
/// the default — and its coverage projection is written by <c>OnCoverageChanged</c> and by nothing else. So
/// the coverage rows here kept the open-ended window the enrolment published, and the engine went on
/// answering Eligible at the counter for a membership that had ended. policy now publishes the row-level
/// <c>CoverageChanged</c> that closes the window; these assert this side actually applies it.</para>
///
/// <para>The reinstatement case is the one that needed a change here rather than upstream. <c>effectiveTo</c>
/// was read only when it arrived as a string, so an explicit null — the only way to say "this window is open
/// again" — was indistinguishable from an absent field and left the termination's end date in place. It now
/// follows the absent-means-unchanged, null-means-clear rule the same handler already applied to
/// <c>waitingPeriodEndsOn</c> and <c>planVersionId</c>. Env-gated on <c>ELIGIBILITY_TEST_DB</c>.</para>
/// </summary>
public class TerminationClosesCoverageTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string? Db =
        Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB")
        ?? Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB_OWNER");

    private static EligibilityDbContext Ctx() => new(new DbContextOptionsBuilder<EligibilityDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static ProjectionUpdater Updater(EligibilityDbContext db) =>
        new(db, new InMemoryEligibilityCache(), TimeProvider.System);

    [SkippableFact]
    public async Task A_termination_event_closes_the_coverage_window()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var policyNo = "TERM-" + Guid.NewGuid().ToString("N")[..10];
        var coverageId = Guid.NewGuid();
        try
        {
            await Seed(policyNo, coverageId, effectiveTo: null);
            (await EndDateOf(policyNo)).Should().BeNull("the enrolment leaves the window open-ended");

            await Apply(coverageId, effectiveTo: "2026-06-30");

            (await EndDateOf(policyNo)).Should().Be(new DateOnly(2026, 6, 30),
                "the end date is what the engine reads to stop covering the member; without it a terminated " +
                "membership still answers Eligible at the counter");
        }
        finally { await Cleanup(policyNo); }
    }

    [SkippableFact]
    public async Task A_reinstatement_event_reopens_the_coverage_window()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var policyNo = "TERM-" + Guid.NewGuid().ToString("N")[..10];
        var coverageId = Guid.NewGuid();
        try
        {
            await Seed(policyNo, coverageId, effectiveTo: new DateOnly(2026, 6, 30));

            await Apply(coverageId, effectiveTo: null);

            (await EndDateOf(policyNo)).Should().BeNull(
                "an explicit null clears the window; while it read as 'unchanged' a reinstated member kept " +
                "the end date their termination wrote and stayed refused with nothing to explain it");
        }
        finally { await Cleanup(policyNo); }
    }

    /// <summary>The other half of the same rule, guarded so the fix above cannot be over-applied: a publisher
    /// that says nothing about the window must not be able to wipe one. Every partial CoverageChanged — a
    /// limit correction, a plan re-point — would otherwise silently reopen a closed membership.</summary>
    [SkippableFact]
    public async Task An_event_that_omits_the_end_date_leaves_it_alone()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var policyNo = "TERM-" + Guid.NewGuid().ToString("N")[..10];
        var coverageId = Guid.NewGuid();
        try
        {
            await Seed(policyNo, coverageId, effectiveTo: new DateOnly(2026, 6, 30));

            await using (var db = Ctx())
            {
                var payload = JsonSerializer.Serialize(new
                {
                    coverageId,
                    beneficiaryId = Guid.NewGuid(),
                    status = "Active",
                }, Web);
                await Updater(db).ApplyAsync(Guid.NewGuid(), "CoverageChanged", payload);
            }

            (await EndDateOf(policyNo)).Should().Be(new DateOnly(2026, 6, 30),
                "absent means unchanged — a partial update must not reopen a terminated membership");
        }
        finally { await Cleanup(policyNo); }
    }

    private static async Task Apply(Guid coverageId, string? effectiveTo)
    {
        await using var db = Ctx();
        // Serialized as the wire carries it: an explicit JSON null, not an omitted property. The distinction
        // between the two is the whole point of the handler's rule, so the fixture must be able to express it.
        var payload = JsonSerializer.Serialize(new
        {
            coverageId,
            beneficiaryId = Guid.NewGuid(),
            status = "Active",
            effectiveTo,
        }, Web);
        await Updater(db).ApplyAsync(Guid.NewGuid(), "CoverageChanged", payload);
    }

    private static async Task Seed(string policyNo, Guid coverageId, DateOnly? effectiveTo)
    {
        await using var db = Ctx();
        db.Coverages.Add(new CoverageProjection
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            CoverageId = coverageId, BeneficiaryId = Guid.NewGuid(),
            BenefitCategory = "CONSULT", PolicyNo = policyNo, Status = "Active",
            EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = effectiveTo,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<DateOnly?> EndDateOf(string policyNo)
    {
        await using var db = Ctx();
        return await db.Coverages.AsNoTracking().Where(c => c.PolicyNo == policyNo)
            .Select(c => c.EffectiveTo).SingleAsync();
    }

    private static async Task Cleanup(string policyNo)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.Coverages.Where(c => c.PolicyNo == policyNo).ExecuteDeleteAsync();
    }
}
