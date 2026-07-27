using FluentAssertions;
using Mersal.Policy.Domain;
using EligibilityDomain = Mersal.Eligibility.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.2 — the guardrail: coverage GENERATED from a plan version must stay shape-compatible with
/// <c>EligibilityEngine</c>.
///
/// This is the seam where the phase-19 product layer meets the benefit spine that phases 1–18 already run on.
/// Generation produces rows; eligibility consumes them; the phase-18 accumulator moves their
/// <c>consumed_value</c>. A mismatch here is not a compile error and not a test failure anywhere else — it is
/// a member who is quietly ineligible for something the plan says they are entitled to, discovered at a
/// counter.
///
/// So this projects real generated coverage into the engine's own view type and asserts the verdict, rather
/// than asserting the two shapes look similar.
/// </summary>
public class CoverageGenerationParityTests
{
    private static readonly Guid LabCategory = Guid.Parse("bbbb0000-0000-0000-0000-000000000001");
    private const string LabCode = "LAB";
    private const string Tenant = "t0";

    private static PlanVersion Version(params BenefitRule[] rules) => new()
    {
        PlanVersionId = Guid.NewGuid(), PlanId = Guid.NewGuid(), VersionNo = 1,
        EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Active, Rules = [.. rules],
    };

    private static BenefitRule LabRule(decimal? limit = 5000m, int waitingDays = 0) => new()
    {
        RuleId = Guid.NewGuid(), BenefitCategoryId = LabCategory, IsCovered = true,
        LimitType = limit is null ? null : LimitType.Annual, LimitValue = limit,
        ResetPeriod = limit is null ? ResetPeriod.None : ResetPeriod.Yearly,
        WaitingPeriodDays = waitingDays,
    };

    private static Enrollment Member(DateOnly from, DateOnly? to = null) => new()
    {
        EnrollmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = Guid.NewGuid(),
        PolicyId = Guid.NewGuid(), PolicyPlanId = Guid.NewGuid(), MemberNo = "MEM-2026-000001",
        EffectiveFrom = from, EffectiveTo = to, Status = EnrollmentStatus.Active,
    };

    /// <summary>Project generated coverage exactly as the eligibility projection does, so the engine under
    /// test is fed the real output rather than a hand-written stand-in.</summary>
    private static EligibilityDomain.CoverageView Project(Coverage coverage, Enrollment enrollment) =>
        new(coverage.CoverageId,
            LabCode,
            coverage.Status == CoverageStatus.Active,
            coverage.EffectiveFrom,
            coverage.EffectiveTo,
            [.. coverage.Limits.Select(l => new EligibilityDomain.LimitState(
                Enum.Parse<EligibilityDomain.LimitType>(l.LimitType.ToString()), l.LimitValue, l.ConsumedValue))],
            enrollment.WaitingPeriodEndsOn);

    private static EligibilityDomain.EligibilityResult Check(
        PlanVersion version, Enrollment enrollment, DateOnly onDate)
    {
        enrollment.WaitingPeriodEndsOn = WaitingPeriod.EndsOn(version, enrollment.EffectiveFrom);
        var generated = CoverageGenerator.Generate(version, enrollment, Tenant);
        return EligibilityDomain.EligibilityEngine.Evaluate(new EligibilityDomain.EligibilityRequest(
            EligibilityDomain.MemberStatus.Active, LabCode, ServiceCode: null, ServiceRequiresPreAuth: false,
            [.. generated.Select(c => Project(c, enrollment))], onDate));
    }

    [Fact]
    public void A_freshly_enrolled_member_is_eligible_for_what_the_plan_covers()
    {
        var result = Check(Version(LabRule()), Member(new(2026, 3, 1)), new(2026, 3, 15));

        result.Decision.Should().Be(EligibilityDomain.EligibilityDecision.Eligible);
        result.LimitState!.LimitValue.Should().Be(5000m);
        result.LimitState.Remaining.Should().Be(5000m, "the accumulator starts at zero");
    }

    [Fact]
    public void An_unlimited_benefit_is_eligible_with_no_binding_limit()
    {
        // The absence of a limit row must not read as "zero remaining" to the engine.
        var result = Check(Version(LabRule(limit: null)), Member(new(2026, 3, 1)), new(2026, 3, 15));

        result.Decision.Should().Be(EligibilityDomain.EligibilityDecision.Eligible);
        result.LimitState.Should().BeNull();
    }

    [Fact]
    public void A_service_inside_the_waiting_period_is_ineligible_with_a_WAITING_PERIOD_reason()
    {
        // THE acceptance criterion. The member holds valid coverage with an intact limit and is still not
        // payable — which is exactly why the engine needed to be told about the waiting period rather than
        // left to infer eligibility from the coverage row alone.
        var result = Check(Version(LabRule(waitingDays: 30)), Member(new(2026, 3, 1)), new(2026, 3, 15));

        result.Decision.Should().Be(EligibilityDomain.EligibilityDecision.Ineligible);
        result.Reasons.Should().ContainSingle().Which.Should().Contain("WAITING_PERIOD");
    }

    [Theory]
    [InlineData("2026-03-30", "Ineligible")]   // last day inside the waiting period
    [InlineData("2026-03-31", "Eligible")]     // first covered day
    public void Cover_begins_the_day_after_the_waiting_period_boundary(string serviceDate, string expected)
    {
        var result = Check(Version(LabRule(waitingDays: 30)), Member(new(2026, 3, 1)),
            DateOnly.Parse(serviceDate, System.Globalization.CultureInfo.InvariantCulture));

        result.Decision.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("2026-12-31", "Eligible")]     // the termination date is STILL covered (inclusive window)
    [InlineData("2027-01-01", "Ineligible")]   // the day after is not
    public void The_generated_coverage_window_is_inclusive_at_its_end(string serviceDate, string expected)
    {
        // Membership windows are inclusive while plan versions are half-open. This asserts the generated rows
        // land on the inclusive side, matching the engine — an off-by-one here turns someone away on the last
        // day of their cover.
        var result = Check(Version(LabRule()), Member(new(2026, 6, 1), new(2026, 12, 31)),
            DateOnly.Parse(serviceDate, System.Globalization.CultureInfo.InvariantCulture));

        result.Decision.ToString().Should().Be(expected);
    }

    [Fact]
    public void An_uncovered_category_produces_no_coverage_and_therefore_no_eligibility()
    {
        var version = Version(new BenefitRule
        {
            RuleId = Guid.NewGuid(), BenefitCategoryId = LabCategory, IsCovered = false,
            ResetPeriod = ResetPeriod.None,
        });

        var result = Check(version, Member(new(2026, 3, 1)), new(2026, 3, 15));

        result.Decision.Should().Be(EligibilityDomain.EligibilityDecision.Ineligible);
        result.Reasons.Should().ContainSingle().Which.Should().Contain("no active coverage");
    }

    [Fact]
    public void An_exhausted_limit_routes_to_authorization_rather_than_a_flat_refusal()
    {
        // Not a generation case, but the one that proves the generated LIMIT shape actually drives the
        // engine's limit branch: consumption is applied to the generated row and the verdict changes.
        var enrollment = Member(new(2026, 3, 1));
        var version = Version(LabRule(limit: 1000m));
        var generated = CoverageGenerator.Generate(version, enrollment, Tenant);
        generated.Single().Limits.Single().ConsumedValue = 1000m;   // as the phase-18 accumulator would leave it

        var result = EligibilityDomain.EligibilityEngine.Evaluate(new EligibilityDomain.EligibilityRequest(
            EligibilityDomain.MemberStatus.Active, LabCode, null, false,
            [.. generated.Select(c => Project(c, enrollment))], new DateOnly(2026, 6, 1)));

        result.Decision.Should().Be(EligibilityDomain.EligibilityDecision.NeedsAuthorization);
        result.LimitState!.Remaining.Should().Be(0m);
    }
}
