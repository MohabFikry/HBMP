using FluentAssertions;
using Mersal.Policy.Api;
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

    // 19.1b — the Active tier catalogue every covered category must price completely.
    private static readonly NetworkTierRef T1 = new(Guid.NewGuid(), "T1");
    private static readonly NetworkTierRef Oon = new(Guid.NewGuid(), "OON");
    private static readonly NetworkTierRef[] ActiveTiers = [T1, Oon];

    private static PlanVersion Draft(params BenefitRule[] rules) => new()
    {
        PlanVersionId = Guid.NewGuid(), PlanId = Guid.NewGuid(), VersionNo = 1,
        EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Draft,
        Rules = [.. rules],
    };

    /// <summary>A rule that prices every Active tier by default, so the pre-19.1b cases below still exercise
    /// exactly what they were written to exercise rather than tripping on the new completeness check.</summary>
    private static BenefitRule Rule(bool covered = true, LimitType? limitType = Domain.LimitType.Annual,
        decimal? limitValue = 5000m, ResetPeriod reset = ResetPeriod.Yearly,
        bool preauth = false, decimal? threshold = null, BenefitRuleTier[]? tiers = null)
    {
        var rule = new BenefitRule
        {
            RuleId = Guid.NewGuid(), BenefitCategoryId = Lab, IsCovered = covered,
            LimitType = limitType, LimitValue = limitValue, ResetPeriod = reset,
            RequiresPreauth = preauth, PreauthCostThreshold = threshold,
        };
        rule.Tiers.AddRange(tiers ?? (covered ? [Tier(T1, copayPercent: 10m), Tier(Oon, copayPercent: 40m)] : []));
        return rule;
    }

    private static BenefitRuleTier Tier(NetworkTierRef tier, bool covered = true, decimal? copayFixed = null,
        decimal? copayPercent = null, decimal? multiplier = null) => new()
    {
        RuleTierId = Guid.NewGuid(), NetworkTierId = tier.NetworkTierId, TierCode = tier.TierCode,
        IsCovered = covered, CopayFixed = copayFixed, CopayPercent = copayPercent, LimitMultiplier = multiplier,
    };

    private static string[] Codes(PlanVersion v) =>
        [.. PlanVersionValidation.Validate(v, Today, ActiveTiers).Select(p => p.Code)];

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

    // ---- 19.1b: the cost-share grid must be COMPLETE ------------------------------------------------------

    [Fact]
    public void A_covered_category_that_leaves_an_active_tier_unpriced_cannot_be_activated()
    {
        // THE 19.1b acceptance case, and the most dangerous shape in this file because nothing about it looks
        // wrong: the plan reads as covered, the tier exists, and adjudication reaches a service delivered
        // there with no agreed member share. Whatever it charges then is a number nobody authored.
        var priced = Rule(tiers: [Tier(T1, copayPercent: 10m)]);

        var codes = Codes(Draft(priced));

        codes.Should().Contain("TIER_NOT_CONFIGURED");
        // The message must name the tier — "some tier is missing" sends the author hunting through a grid.
        PlanVersionValidation.Validate(Draft(priced), Today, ActiveTiers)
            .Single(p => p.Code == "TIER_NOT_CONFIGURED").Detail.Should().Contain("OON");
    }

    [Fact]
    public void Not_covered_at_this_tier_is_a_valid_statement_and_not_a_gap()
    {
        // An HMO that pays nothing out-of-network is ordinary benefit design. The distinction the validator
        // draws is between SAYING nothing is covered there and saying nothing at all.
        var rule = Rule(tiers: [Tier(T1, copayPercent: 10m), Tier(Oon, covered: false)]);

        Codes(Draft(rule)).Should().BeEmpty();
    }

    [Fact]
    public void An_uncovered_category_may_not_carry_a_per_tier_cost_share()
    {
        var rule = Rule(covered: false, limitType: null, limitValue: null, reset: ResetPeriod.None,
            tiers: [Tier(T1, copayPercent: 10m)]);
        rule.BenefitCategoryId = Guid.NewGuid();

        Codes(Draft(Rule(), rule)).Should().Contain("UNCOVERED_WITH_TIER_COST_SHARE");
    }

    [Fact]
    public void A_tier_that_is_no_longer_active_cannot_be_priced()
    {
        // A draft written before a tier was retired must not activate against it: the resolver will never
        // return that tier again, so the row is cost share for a situation that can no longer arise.
        var retired = new NetworkTierRef(Guid.NewGuid(), "T3");
        var rule = Rule(tiers: [Tier(T1, copayPercent: 10m), Tier(Oon, copayPercent: 40m), Tier(retired)]);

        Codes(Draft(rule)).Should().Contain("UNKNOWN_TIER");
    }

    [Fact]
    public void A_tier_may_not_be_priced_twice_in_one_category()
    {
        var rule = Rule(tiers:
            [Tier(T1, copayPercent: 10m), Tier(T1, copayPercent: 25m), Tier(Oon, copayPercent: 40m)]);

        Codes(Draft(rule)).Should().Contain("DUPLICATE_TIER");
    }

    [Fact]
    public void A_tier_may_not_set_both_a_fixed_and_a_percentage_copay()
    {
        var rule = Rule(tiers:
            [Tier(T1, copayFixed: 50m, copayPercent: 10m), Tier(Oon, copayPercent: 40m)]);

        Codes(Draft(rule)).Should().Contain("BOTH_COPAY_FORMS");
    }

    [Fact]
    public void A_covered_tier_with_a_zero_limit_multiplier_is_rejected()
    {
        // Same failure as ZERO_LIMIT one level down: it renders as covered and entitles nothing.
        var rule = Rule(tiers:
            [Tier(T1, copayPercent: 10m), Tier(Oon, copayPercent: 40m, multiplier: 0m)]);

        Codes(Draft(rule)).Should().Contain("ZERO_TIER_MULTIPLIER");
    }

    [Fact]
    public void With_no_tiers_configured_at_all_every_active_tier_is_reported()
    {
        // The empty-grid case: an author who never opened the cost-share tab gets one problem per tier, not a
        // single vague complaint.
        var rule = Rule(tiers: []);

        var problems = PlanVersionValidation.Validate(Draft(rule), Today, ActiveTiers);

        problems.Count(p => p.Code == "TIER_NOT_CONFIGURED").Should().Be(2);
    }

    // ---- 19.1b: what applies AT a tier, resolving overrides against the rule ------------------------------

    [Fact]
    public void A_tier_override_decides_pre_authorization_and_falls_back_to_the_rule()
    {
        var rule = Rule(preauth: false);
        var inNetwork = Tier(T1);
        var outOfNetwork = Tier(Oon);
        outOfNetwork.RequiresPreauthOverride = true;

        // The common real configuration: open access in-network, authorization required outside it.
        inNetwork.ResolvesPreauth(rule).Should().BeFalse("no override — inherit the rule's default");
        outOfNetwork.ResolvesPreauth(rule).Should().BeTrue();
    }

    [Fact]
    public void A_tier_multiplier_scales_the_rules_limit_but_never_invents_one()
    {
        var rule = Rule(limitValue: 5000m);

        Tier(T1).ResolvesLimit(rule).Should().Be(5000m, "no multiplier — the rule's own limit applies");
        Tier(Oon, multiplier: 0.5m).ResolvesLimit(rule).Should().Be(2500m);

        // An unlimited benefit stays unlimited: multiplying "no ceiling" by a half is not half a ceiling.
        var unlimited = Rule(limitType: null, limitValue: null, reset: ResetPeriod.None);
        Tier(Oon, multiplier: 0.5m).ResolvesLimit(unlimited).Should().BeNull();
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

/// <summary>
/// Phase 19.6 — the read/write round trip of a benefit rule set.
///
/// <para>The editor reads a draft, lets an administrator change it, and writes the whole set back. That only
/// works if the two directions agree on how a benefit category is identified. They did not: the response
/// carried <c>benefitCategoryId</c> while <see cref="BenefitRuleInput"/> is keyed by CODE, so a client could
/// read a draft and had no way to re-submit it without a catalogue it was never given. The projection now
/// carries the code, and this pins it.</para>
/// </summary>
public sealed class BenefitRuleProjectionTests
{
    private static readonly Guid Lab = Guid.NewGuid();
    private static readonly Guid Pharmacy = Guid.NewGuid();

    private static BenefitRule Rule(Guid categoryId) => new()
    {
        RuleId = Guid.NewGuid(), BenefitCategoryId = categoryId, IsCovered = true,
        LimitType = LimitType.Annual, LimitValue = 1000m, ResetPeriod = ResetPeriod.Yearly,
    };

    [Fact]
    public void A_projected_rule_names_the_category_the_write_path_expects()
    {
        var codes = new Dictionary<Guid, string> { [Lab] = "LAB", [Pharmacy] = "PHARMACY" };

        var view = BenefitRuleView.From(Rule(Lab), codes);

        view.BenefitCategoryCode.Should().Be("LAB");
        view.BenefitCategoryId.Should().Be(Lab);
    }

    [Fact]
    public void A_category_the_catalogue_does_not_know_projects_a_null_code_rather_than_a_guess()
    {
        var view = BenefitRuleView.From(Rule(Guid.NewGuid()), new Dictionary<Guid, string> { [Lab] = "LAB" });

        // Null is the honest answer. Falling back to the id as a "code" would produce a rule set that
        // round-trips cleanly right up to the moment the service rejects UNKNOWN_BENEFIT_CATEGORY.
        view.BenefitCategoryCode.Should().BeNull();
    }

    [Fact]
    public void A_version_projected_without_a_catalogue_still_returns_its_rules()
    {
        var version = new PlanVersion
        {
            PlanVersionId = Guid.NewGuid(), PlanId = Guid.NewGuid(), VersionNo = 1,
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Draft,
            Rules = [Rule(Lab)],
        };

        var view = PlanVersionView.From(version);

        view.Rules.Should().HaveCount(1);
        view.Rules[0].BenefitCategoryCode.Should().BeNull();
    }
}
