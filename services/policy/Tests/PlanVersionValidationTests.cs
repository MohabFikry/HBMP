using FluentAssertions;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.1 — the activation validation matrix and the half-open window, as pure functions.
///
/// Activation is the irreversible step: once a version is Active its benefit configuration can never be
/// edited again (only superseded by a new version). Everything this validator catches is therefore something
/// that would otherwise be frozen into the plan permanently, and every case here is a configuration that is
/// syntactically legal — the database's CHECK constraints already reject the malformed ones — but means
/// something nobody intended.
/// </summary>
public class PlanVersionValidationTests
{
    private static readonly DateOnly Today = new(2026, 7, 1);
    private static readonly Guid Lab = Guid.NewGuid();

    private static PlanVersion Draft(params BenefitRule[] rules) => new()
    {
        PlanVersionId = Guid.NewGuid(), PlanId = Guid.NewGuid(), VersionNo = 1,
        EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Draft,
        Rules = [.. rules],
    };

    private static BenefitRule Rule(bool covered = true, LimitType? limitType = Domain.LimitType.Annual,
        decimal? limitValue = 5000m, ResetPeriod reset = ResetPeriod.Yearly,
        bool preauth = false, decimal? threshold = null, decimal? copayFixed = null, decimal? copayPercent = null) => new()
    {
        RuleId = Guid.NewGuid(), BenefitCategoryId = Lab, IsCovered = covered,
        LimitType = limitType, LimitValue = limitValue, ResetPeriod = reset,
        RequiresPreauth = preauth, PreauthCostThreshold = threshold,
        CopayFixed = copayFixed, CopayPercent = copayPercent,
    };

    private static string[] Codes(PlanVersion v) => [.. PlanVersionValidation.Validate(v, Today).Select(p => p.Code)];

    [Fact]
    public void A_well_formed_draft_validates()
    {
        Codes(Draft(Rule())).Should().BeEmpty();
    }

    [Fact]
    public void A_version_that_configures_nothing_cannot_be_activated()
    {
        Codes(Draft()).Should().Contain("NO_RULES");
    }

    [Fact]
    public void A_version_that_covers_nothing_cannot_be_activated()
    {
        // Every category present but every one excluded: structurally valid, and an entitlement of nothing.
        Codes(Draft(Rule(covered: false, limitType: null, limitValue: null, reset: ResetPeriod.None)))
            .Should().Contain("NO_COVERED_CATEGORY");
    }

    [Fact]
    public void An_uncovered_category_may_not_carry_benefits()
    {
        // Reads as "not covered" in one place and "5000 EGP" in another — whichever the reader trusts is a coin flip.
        var uncoveredButFunded = Rule(covered: false);
        uncoveredButFunded.BenefitCategoryId = Guid.NewGuid();
        Codes(Draft(Rule(), uncoveredButFunded)).Should().Contain("UNCOVERED_WITH_BENEFITS");
    }

    [Fact]
    public void A_covered_category_with_a_zero_limit_is_rejected()
    {
        // The UI shows a tick; the member is entitled to nothing. Say "not covered" and mean it.
        Codes(Draft(Rule(limitValue: 0m))).Should().Contain("ZERO_LIMIT");
    }

    [Fact]
    public void A_reset_period_without_a_limit_is_rejected()
    {
        Codes(Draft(Rule(limitType: null, limitValue: null, reset: ResetPeriod.Monthly)))
            .Should().Contain("RESET_WITHOUT_LIMIT");
    }

    [Fact]
    public void A_lifetime_limit_cannot_reset()
    {
        Codes(Draft(Rule(limitType: LimitType.Lifetime, reset: ResetPeriod.Yearly)))
            .Should().Contain("LIFETIME_RESET");
    }

    [Fact]
    public void A_preauth_threshold_above_the_benefit_limit_can_never_fire()
    {
        Codes(Draft(Rule(preauth: true, threshold: 9000m, limitValue: 5000m)))
            .Should().Contain("THRESHOLD_ABOVE_LIMIT");
    }

    [Fact]
    public void A_window_that_ends_before_it_starts_is_rejected()
    {
        var v = Draft(Rule());
        v.EffectiveTo = v.EffectiveFrom.AddDays(-1);
        Codes(v).Should().Contain("BAD_WINDOW");
    }

    [Fact]
    public void A_window_that_has_already_elapsed_is_rejected()
    {
        var v = Draft(Rule());
        v.EffectiveTo = Today.AddDays(-1);
        Codes(v).Should().Contain("WINDOW_ELAPSED");
    }

    [Fact]
    public void Only_a_draft_can_be_activated()
    {
        var v = Draft(Rule());
        v.Status = PlanVersionStatus.Active;
        Codes(v).Should().Contain("NOT_DRAFT");
    }

    [Fact]
    public void Every_problem_is_reported_at_once_not_just_the_first()
    {
        // An author fixing a plan should see the whole list; drip-feeding one error per attempt turns a
        // five-minute correction into five round trips through an irreversible action.
        var v = Draft(Rule(limitValue: 0m, limitType: LimitType.Lifetime, reset: ResetPeriod.Yearly));
        Codes(v).Should().Contain(["ZERO_LIMIT", "LIFETIME_RESET"]);
    }

    // ---- The half-open window, which is where off-by-one errors become wrong adjudications ----------------

    [Theory]
    [InlineData("2025-12-31", false)]   // the day before it starts
    [InlineData("2026-01-01", true)]    // effective_from is INCLUSIVE — the first day is in force
    [InlineData("2026-06-30", true)]
    [InlineData("2026-07-01", false)]   // effective_to is EXCLUSIVE — this day belongs to the successor
    [InlineData("2026-07-02", false)]
    public void Covers_treats_the_window_as_half_open(string date, bool expected)
    {
        var v = Draft(Rule());
        v.EffectiveTo = new DateOnly(2026, 7, 1);
        v.Covers(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(expected);
    }

    [Fact]
    public void An_open_ended_version_covers_every_date_from_its_start()
    {
        var v = Draft(Rule());   // effective_to null
        v.Covers(new DateOnly(2026, 1, 1)).Should().BeTrue();
        v.Covers(new DateOnly(2099, 1, 1)).Should().BeTrue();
        v.Covers(new DateOnly(2025, 12, 31)).Should().BeFalse();
    }
}
