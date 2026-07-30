using System.Text.Json;
using FluentAssertions;
using Mersal.Eligibility.Domain;
using Mersal.Eligibility.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Tests;

/// <summary>
/// Phase 24 Gate 3 — INV-ELIGIBILITY-MATRIX, proven through the REAL projection path.
///
/// <para>Two arms of the decision matrix were unreachable in the running system, and neither showed up as a
/// failing test, because every existing test around this seam either builds a
/// <see cref="CoverageView"/> by hand or calls the pure engine directly. Both arms are user-visible as care
/// refused or care granted at a counter:</para>
///
/// <list type="number">
/// <item><b>An enrolled member had no coverage at all.</b> eligibility-service's coverage projection is
/// written by exactly one handler — <c>OnCoverageChanged</c> — and the only publisher of
/// <c>CoverageChanged</c> was the manual <c>POST /policies/{id}/coverages</c> endpoint. Enrolling through the
/// PAS membership path (19.2, the path the product actually uses) published <c>CoverageGenerated</c>
/// carrying a COUNT of categories and nothing else, and eligibility ignores that event. So a correctly
/// enrolled member reached the engine with zero coverage rows and came back
/// <c>Ineligible — "no active coverage for LAB"</c>. Entitlement the plan grants, refused at the desk.</item>
///
/// <item><b>The waiting period could never fire.</b> <c>CoverageProjection</c> had no
/// <c>WaitingPeriodEndsOn</c> column and <c>EligibilityChecker.ComputeAsync</c> built its
/// <see cref="CoverageView"/> without one, so the 19.2 branch was dead code in production: a member inside
/// their waiting period was returned <c>Eligible</c>. The engine's own unit tests passed throughout — they
/// construct the view directly and pass the date the real caller never had.</item>
/// </list>
///
/// <para>So these tests drive the projection handler with the payload policy-service publishes and then ask
/// the real <see cref="EligibilityChecker"/>. Nothing here hand-builds a CoverageView; that is the whole
/// point. Env-gated on <c>ELIGIBILITY_TEST_DB</c>; self-cleans by beneficiary id.</para>
/// </summary>
public class EnrolledMemberEligibilityTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string? Db =
        Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB")
        ?? Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB_OWNER");

    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Category = "LAB";

    private static EligibilityDbContext Ctx() => new(new DbContextOptionsBuilder<EligibilityDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    /// <summary>The payload policy-service publishes for one generated coverage. Field names are the
    /// contract: <c>ProjectionUpdater.OnCoverageChanged</c> reads exactly these.</summary>
    private static string CoverageEvent(
        Guid coverageId, Guid beneficiaryId, DateOnly from, DateOnly? waitingEndsOn, decimal limit = 5000m) =>
        JsonSerializer.Serialize(new
        {
            tenantId = Tenant,
            coverageId,
            beneficiaryId,
            category = Category,
            status = "Active",
            policyNo = "POL-24-3",
            effectiveFrom = from.ToString("yyyy-MM-dd"),
            effectiveTo = (string?)null,
            waitingPeriodEndsOn = waitingEndsOn?.ToString("yyyy-MM-dd"),
            limits = new[] { new { limitType = "Annual", limitValue = limit, consumedValue = 0m } },
        }, Web);

    private static async Task<EligibilityResult> CheckAsync(Guid beneficiaryId, DateOnly today)
    {
        await using var db = Ctx();
        var checker = new EligibilityChecker(
            db, new InMemoryEligibilityCache(), TimeProvider.System, new FixedCalendar(today));
        var outcome = await checker.CheckAsync(beneficiaryId, Category, serviceCode: null, serviceRequiresPreAuth: false);
        return outcome.Result;
    }

    /// <summary>The member IS enrolled and the plan DOES cover the category. Before the fix this returned
    /// Ineligible "no active coverage for LAB", because the enrolment path's event never reached here.</summary>
    [SkippableFact]
    public async Task An_enrolled_member_is_eligible_for_a_category_their_plan_covers()
    {
        Skip.If(Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        var beneficiary = Guid.NewGuid();
        var enrolledOn = new DateOnly(2026, 1, 1);
        try
        {
            await SeedActiveMember(beneficiary);
            await ApplyCoverage(CoverageEvent(Guid.NewGuid(), beneficiary, enrolledOn, waitingEndsOn: null));

            var result = await CheckAsync(beneficiary, today: new DateOnly(2026, 3, 1));

            result.Decision.Should().Be(EligibilityDecision.Eligible,
                "the member holds active coverage generated by their enrolment — refusing them here is the " +
                "platform denying an entitlement its own plan grants");
            result.Reasons.Should().NotContain(r => r.Contains("no active coverage", StringComparison.OrdinalIgnoreCase));
        }
        finally { await Cleanup(beneficiary); }
    }

    /// <summary>19.2 — covered, limits intact, and NOT yet payable. A hard Ineligible: no approval can
    /// shorten a waiting period.</summary>
    [SkippableFact]
    public async Task A_member_inside_their_waiting_period_is_ineligible_not_eligible()
    {
        Skip.If(Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        var beneficiary = Guid.NewGuid();
        var enrolledOn = new DateOnly(2026, 1, 1);
        try
        {
            await SeedActiveMember(beneficiary);
            // 30-day waiting period: the last day inside it is 30 Jan.
            await ApplyCoverage(CoverageEvent(Guid.NewGuid(), beneficiary, enrolledOn, new DateOnly(2026, 1, 30)));

            var inside = await CheckAsync(beneficiary, today: new DateOnly(2026, 1, 15));
            inside.Decision.Should().Be(EligibilityDecision.Ineligible,
                "the waiting period exists to exclude exactly this window; returning Eligible pays a claim " +
                "the policy does not cover");
            inside.Reasons.Should().Contain(r => r.StartsWith("WAITING_PERIOD", StringComparison.Ordinal));
        }
        finally { await Cleanup(beneficiary); }
    }

    /// <summary>The boundary is the LAST day inside the period, so the day after it is payable. Without this
    /// the fix could be "always ineligible" and the test above would still pass.</summary>
    [SkippableFact]
    public async Task The_day_after_the_waiting_period_ends_is_payable()
    {
        Skip.If(Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        var beneficiary = Guid.NewGuid();
        try
        {
            await SeedActiveMember(beneficiary);
            await ApplyCoverage(CoverageEvent(
                Guid.NewGuid(), beneficiary, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 30)));

            (await CheckAsync(beneficiary, new DateOnly(2026, 1, 30))).Decision
                .Should().Be(EligibilityDecision.Ineligible, "the stored date is the last day INSIDE the period");
            (await CheckAsync(beneficiary, new DateOnly(2026, 1, 31))).Decision
                .Should().Be(EligibilityDecision.Eligible, "cover begins the day after the boundary");
        }
        finally { await Cleanup(beneficiary); }
    }

    // ---- harness -----------------------------------------------------------------------------------------

    private static async Task ApplyCoverage(string payload)
    {
        await using var db = Ctx();
        var updater = new ProjectionUpdater(db, new InMemoryEligibilityCache(), TimeProvider.System);
        (await updater.ApplyAsync(Guid.NewGuid(), "CoverageChanged", payload)).Should().BeTrue();
    }

    private static async Task SeedActiveMember(Guid beneficiaryId)
    {
        await using var db = Ctx();
        db.Members.Add(new MemberProjection
        {
            TenantId = Tenant, BeneficiaryId = beneficiaryId, GivenName = "Gate", FamilyName = "Three",
            Status = "Active", UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task Cleanup(Guid beneficiaryId)
    {
        await using var db = Ctx();
        await db.Snapshots.Where(s => s.BeneficiaryId == beneficiaryId).ExecuteDeleteAsync();
        await db.Coverages.Where(c => c.BeneficiaryId == beneficiaryId).ExecuteDeleteAsync();
        await db.Members.Where(m => m.BeneficiaryId == beneficiaryId).ExecuteDeleteAsync();
    }

    /// <summary>Pins "today" so a waiting-period boundary is asserted against a date, not against the
    /// wall clock — the same reason the engine takes OnDate rather than reading the clock itself.</summary>
    private sealed class FixedCalendar(DateOnly today) : IBusinessCalendar
    {
        public DateOnly Today() => today;
        public DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
        public TimeZoneInfo Zone => TimeZoneInfo.Utc;
    }
}
