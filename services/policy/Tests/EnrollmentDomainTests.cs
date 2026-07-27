using FluentAssertions;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.2 + 19.2b — the pure membership logic: coverage generation, waiting periods, plan election, and
/// the plan-change carry-forward arithmetic.
///
/// The generation tests matter most. A member's entitlement is DERIVED from a plan version, so a defect here
/// does not produce an error — it produces a person who is quietly covered for the wrong things, and nothing
/// downstream can tell because everything downstream trusts these rows.
/// </summary>
public class EnrollmentDomainTests
{
    private static readonly Guid Lab = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid Pharmacy = Guid.Parse("aaaa0000-0000-0000-0000-000000000002");
    private static readonly Guid Imaging = Guid.Parse("aaaa0000-0000-0000-0000-000000000003");
    private const string Tenant = "t0";

    private static PlanVersion Version(params BenefitRule[] rules) => new()
    {
        PlanVersionId = Guid.NewGuid(), PlanId = Guid.NewGuid(), VersionNo = 1,
        EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Active,
        Rules = [.. rules],
    };

    private static BenefitRule Rule(Guid category, bool covered = true,
        LimitType? limitType = LimitType.Annual, decimal? limitValue = 5000m,
        ResetPeriod reset = ResetPeriod.Yearly, int waitingDays = 0) => new()
    {
        RuleId = Guid.NewGuid(), BenefitCategoryId = category, IsCovered = covered,
        LimitType = limitType, LimitValue = limitValue, ResetPeriod = reset, WaitingPeriodDays = waitingDays,
    };

    private static Enrollment Member(DateOnly from, DateOnly? to = null) => new()
    {
        EnrollmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = Guid.NewGuid(),
        PolicyId = Guid.NewGuid(), PolicyPlanId = Guid.NewGuid(), MemberNo = "MEM-2026-000001",
        EffectiveFrom = from, EffectiveTo = to, Status = EnrollmentStatus.Active,
    };

    // ---- Coverage generation fidelity --------------------------------------------------------------------

    [Fact]
    public void Coverage_is_generated_for_every_covered_category_and_no_others()
    {
        // An uncovered category must produce NOTHING. A coverage row that exists but grants nothing reads as
        // an entitlement in every screen and report that renders it.
        var version = Version(Rule(Lab), Rule(Pharmacy), Rule(Imaging, covered: false, limitType: null, limitValue: null));

        var generated = CoverageGenerator.Generate(version, Member(new(2026, 3, 1)), Tenant);

        generated.Should().HaveCount(2);
        generated.Select(c => c.BenefitCategoryId).Should().BeEquivalentTo([Lab, Pharmacy]);
    }

    [Fact]
    public void The_generated_limit_mirrors_the_rule_exactly()
    {
        var version = Version(Rule(Lab, limitType: LimitType.Count, limitValue: 12m, reset: ResetPeriod.Monthly));

        var limit = CoverageGenerator.Generate(version, Member(new(2026, 3, 1)), Tenant).Single().Limits.Single();

        limit.LimitType.Should().Be(LimitType.Count);
        limit.LimitValue.Should().Be(12m);
        limit.ResetPeriod.Should().Be(ResetPeriod.Monthly);
    }

    [Fact]
    public void A_generated_accumulator_starts_at_zero()
    {
        // THE guardrail. Phase 18 owns consumed_value and is its only writer; generation initializes it and
        // nothing here ever moves it again. Seeding it non-zero is the X1 bug class the spine was rebuilt to close.
        var generated = CoverageGenerator.Generate(Version(Rule(Lab)), Member(new(2026, 3, 1)), Tenant);

        generated.Single().Limits.Single().ConsumedValue.Should().Be(0m);
    }

    [Fact]
    public void An_unlimited_category_generates_coverage_with_no_limit_row()
    {
        // Covered-but-unlimited is legitimate, and is represented by the ABSENCE of a limit rather than by a
        // sentinel value every downstream calculation would then have to special-case.
        var version = Version(Rule(Lab, limitType: null, limitValue: null, reset: ResetPeriod.None));

        var coverage = CoverageGenerator.Generate(version, Member(new(2026, 3, 1)), Tenant).Single();

        coverage.Limits.Should().BeEmpty();
    }

    [Fact]
    public void The_coverage_window_is_the_ENROLMENT_window_not_the_plan_versions()
    {
        // Deriving it from the plan version would cover a member for days they were not enrolled, and leave
        // them uncovered on days they were.
        var version = Version(Rule(Lab));           // version starts 2026-01-01
        var enrollment = Member(new(2026, 6, 1), new(2026, 12, 31));

        var coverage = CoverageGenerator.Generate(version, enrollment, Tenant).Single();

        coverage.EffectiveFrom.Should().Be(new DateOnly(2026, 6, 1));
        coverage.EffectiveTo.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void Generated_coverage_carries_the_beneficiary_policy_and_tenant()
    {
        var enrollment = Member(new(2026, 3, 1));

        var coverage = CoverageGenerator.Generate(Version(Rule(Lab)), enrollment, Tenant).Single();

        coverage.BeneficiaryId.Should().Be(enrollment.BeneficiaryId);
        coverage.PolicyId.Should().Be(enrollment.PolicyId);
        coverage.TenantId.Should().Be(Tenant);
        coverage.Status.Should().Be(CoverageStatus.Active);
        coverage.Limits.Single().CoverageId.Should().Be(coverage.CoverageId);
    }

    // ---- Waiting periods ---------------------------------------------------------------------------------

    [Fact]
    public void The_waiting_period_boundary_is_the_LAST_day_inside_it()
    {
        // 30 days from 1 March means 30 March is the last waiting day and 31 March is the first covered one.
        // Storing "the last day inside" rather than "the first day after" is what keeps the member's card,
        // eligibility and claims from each landing on a different day.
        var version = Version(Rule(Lab, waitingDays: 30));

        var ends = WaitingPeriod.EndsOn(version, new DateOnly(2026, 3, 1));

        ends.Should().Be(new DateOnly(2026, 3, 30));
    }

    [Fact]
    public void Zero_waiting_days_stores_no_boundary_at_all()
    {
        // A stored date would read as "one day of waiting" to anyone rendering it.
        WaitingPeriod.EndsOn(Version(Rule(Lab, waitingDays: 0)), new DateOnly(2026, 3, 1)).Should().BeNull();
    }

    [Fact]
    public void The_longest_waiting_period_across_covered_categories_wins()
    {
        // The enrolment-level date is when the member's whole package is live; per-category dates are resolved
        // from the rules at check time.
        var version = Version(Rule(Lab, waitingDays: 30), Rule(Pharmacy, waitingDays: 90));

        WaitingPeriod.EndsOn(version, new DateOnly(2026, 1, 1)).Should().Be(new DateOnly(2026, 3, 31));
    }

    [Fact]
    public void An_uncovered_categorys_waiting_period_is_ignored()
    {
        var version = Version(Rule(Lab, waitingDays: 30),
            Rule(Imaging, covered: false, limitType: null, limitValue: null, waitingDays: 365));

        WaitingPeriod.EndsOn(version, new DateOnly(2026, 1, 1)).Should().Be(new DateOnly(2026, 1, 30));
    }

    [Fact]
    public void A_member_is_inside_the_waiting_period_up_to_and_including_the_boundary()
    {
        var member = Member(new(2026, 3, 1));
        member.WaitingPeriodEndsOn = new DateOnly(2026, 3, 30);

        member.InWaitingPeriod(new DateOnly(2026, 3, 30)).Should().BeTrue("the boundary day is still inside");
        member.InWaitingPeriod(new DateOnly(2026, 3, 31)).Should().BeFalse("cover starts the next day");
    }

    // ---- The INCLUSIVE membership window -----------------------------------------------------------------

    [Theory]
    [InlineData("2026-05-31", false)]   // before it starts
    [InlineData("2026-06-01", true)]    // first day
    [InlineData("2026-12-31", true)]    // termination date — STILL COVERED (inclusive, unlike plan_version)
    [InlineData("2027-01-01", false)]   // the day after
    public void The_membership_window_is_inclusive_at_both_ends(string date, bool expected)
    {
        // plan_version is half-open; enrolment and coverage are inclusive, matching the shipped
        // EligibilityEngine. Terminating "effective 31 December" must not silently end cover on the 30th.
        var member = Member(new(2026, 6, 1), new(2026, 12, 31));

        member.Covers(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(expected);
    }

    [Fact]
    public void Suspended_still_occupies_the_beneficiarys_slot()
    {
        // The overlap exclusion treats Suspended as live: a suspension pauses the benefit, it does not vacate
        // the membership, so a second enrolment must not slide in underneath it.
        var suspended = Member(new(2026, 1, 1));
        suspended.Status = EnrollmentStatus.Suspended;
        suspended.IsLive.Should().BeTrue();

        var terminated = Member(new(2026, 1, 1));
        terminated.Status = EnrollmentStatus.Terminated;
        terminated.IsLive.Should().BeFalse();
    }

    // ---- Plan election (19.2b) ---------------------------------------------------------------------------

    [Fact]
    public void No_rule_means_no_restriction()
    {
        PlanEligibility.Evaluate(PlanEligibility.Parse(null),
            new ElectionCandidate(null, Relationship.Principal, 30, null)).Should().BeEmpty();
    }

    [Fact]
    public void A_group_restricted_plan_names_the_criterion_it_refused_on()
    {
        // The acceptance case: Oncology restricted to the Oncology group. "Not eligible" alone sends an officer
        // hunting through a plan definition they may not be able to see.
        var oncology = Guid.NewGuid();
        var rule = PlanEligibility.Parse($$"""{"groupIds":["{{oncology}}"]}""");

        var failures = PlanEligibility.Evaluate(rule, new ElectionCandidate(Guid.NewGuid(), Relationship.Principal, 40, null));

        failures.Should().ContainSingle().Which.Criterion.Should().Be("groupIds");
    }

    [Fact]
    public void A_member_of_the_named_group_is_admitted()
    {
        var oncology = Guid.NewGuid();
        var rule = PlanEligibility.Parse($$"""{"groupIds":["{{oncology}}"]}""");

        PlanEligibility.Evaluate(rule, new ElectionCandidate(oncology, Relationship.Principal, 40, null))
            .Should().BeEmpty();
    }

    [Fact]
    public void Criteria_are_conjunctive_not_alternative()
    {
        // A rule naming a group AND a relationship that accepted EITHER would let a member onto a restricted
        // plan by satisfying the looser half — the opposite of what a restriction is for.
        var group = Guid.NewGuid();
        var rule = PlanEligibility.Parse($$"""{"groupIds":["{{group}}"],"relationships":["Principal"]}""");

        var failures = PlanEligibility.Evaluate(rule, new ElectionCandidate(group, Relationship.Child, 10, null));

        failures.Should().ContainSingle().Which.Criterion.Should().Be("relationships");
    }

    [Theory]
    [InlineData(17, "minAge")]
    [InlineData(70, "maxAge")]
    public void Age_bands_are_enforced_at_both_ends(int age, string expectedCriterion)
    {
        var rule = PlanEligibility.Parse("""{"minAge":18,"maxAge":64}""");

        PlanEligibility.Evaluate(rule, new ElectionCandidate(null, Relationship.Principal, age, null))
            .Should().ContainSingle().Which.Criterion.Should().Be(expectedCriterion);
    }

    [Fact]
    public void An_unknown_age_fails_an_age_banded_plan_rather_than_passing_it()
    {
        // Not knowing whether someone qualifies is not the same as them qualifying.
        PlanEligibility.Evaluate(PlanEligibility.Parse("""{"minAge":18}"""),
            new ElectionCandidate(null, Relationship.Principal, null, null))
            .Should().ContainSingle().Which.Criterion.Should().Be("minAge");
    }

    [Fact]
    public void Every_failed_criterion_is_reported_not_just_the_first()
    {
        var rule = PlanEligibility.Parse("""{"relationships":["Principal"],"minAge":18}""");

        PlanEligibility.Evaluate(rule, new ElectionCandidate(null, Relationship.Child, 8, null))
            .Select(f => f.Criterion).Should().BeEquivalentTo(["relationships", "minAge"]);
    }

    [Fact]
    public void A_malformed_rule_is_flagged_rather_than_treated_as_unrestricted()
    {
        // A typo in a plan's eligibility rule must not silently unlock a restricted plan.
        PlanEligibility.IsMalformed("{ this is not json ").Should().BeTrue();
        PlanEligibility.IsMalformed(null).Should().BeFalse();
        PlanEligibility.IsMalformed("""{"minAge":18}""").Should().BeFalse();
    }

    // ---- Plan-change carry-forward (ADR-0020) ------------------------------------------------------------

    [Fact]
    public void Consumption_carries_forward_so_a_plan_change_is_not_a_fresh_ceiling()
    {
        // THE acceptance case: 300 consumed of a 1,000 Lab limit, moving to a 500 plan → 200 remaining, not 500.
        var carried = ConsumptionCarryForward.Apply(
            new CategoryCarryForward(Lab, ConsumedValue: 300m, NewLimitValue: 500m),
            PlanChangeConsumptionPolicy.CarryForward);

        carried.LimitValue.Should().Be(500m);
        carried.ConsumedValue.Should().Be(300m);
        carried.Remaining.Should().Be(200m);
        carried.Exhausted.Should().BeFalse();
    }

    [Fact]
    public void Remaining_is_floored_at_zero_never_negative()
    {
        // 800 used under a 1,000 plan, moving to a 500 plan. Minus 300 would propagate into every display and
        // comparison downstream.
        var carried = ConsumptionCarryForward.Apply(
            new CategoryCarryForward(Lab, 800m, 500m), PlanChangeConsumptionPolicy.CarryForward);

        carried.Remaining.Should().Be(0m);
        carried.Exhausted.Should().BeTrue();
    }

    [Fact]
    public void The_reset_per_plan_alternative_is_a_setting_and_behaves_differently()
    {
        // ADR-0020 is UNSIGNED. Both answers are implemented so a reversal is a configuration change, not a
        // migration of every member's accumulator.
        var input = new CategoryCarryForward(Lab, 300m, 500m);

        ConsumptionCarryForward.Apply(input, PlanChangeConsumptionPolicy.CarryForward).Remaining.Should().Be(200m);
        ConsumptionCarryForward.Apply(input, PlanChangeConsumptionPolicy.ResetPerPlan).Remaining.Should().Be(500m);
    }

    [Fact]
    public void An_unlimited_category_is_never_exhausted_under_either_policy()
    {
        foreach (var policy in Enum.GetValues<PlanChangeConsumptionPolicy>())
        {
            var carried = ConsumptionCarryForward.Apply(new CategoryCarryForward(Lab, 9999m, null), policy);
            carried.Remaining.Should().BeNull();
            carried.Exhausted.Should().BeFalse();
        }
    }
}
