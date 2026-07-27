using System.Reflection;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.5 — the pure rules behind policy query, member query and coverage details (design 38 §4.4–§4.6).
///
/// Everything here runs without a database because everything here is a DECISION rather than a lookup: which
/// band a member falls in, whether a sort field is allowed, what a payer restriction permits, and what a
/// caller's role lets them see. Those are the parts that are wrong quietly.
/// </summary>
public class AdministrativeQueryTests
{
    // ---- Utilization bands -------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 0, UtilizationBand.Zero)]
    [InlineData(100, 1, UtilizationBand.Low)]
    [InlineData(100, 49.9, UtilizationBand.Low)]
    [InlineData(100, 50, UtilizationBand.Medium)]
    [InlineData(100, 79.9, UtilizationBand.Medium)]
    [InlineData(100, 80, UtilizationBand.High)]
    [InlineData(100, 99.9, UtilizationBand.High)]
    [InlineData(100, 100, UtilizationBand.Exhausted)]
    public void Bands_partition_the_percentage(decimal limit, decimal consumed, UtilizationBand expected) =>
        UtilizationBands.Of(limit, consumed, hasCoverage: true).Should().Be(expected);

    [Fact]
    public void Over_consumption_stays_in_the_exhausted_band_rather_than_being_clamped()
    {
        // A limit reduced mid-period legitimately leaves consumed above limit. That row is the one worth
        // reading; clamping it to 100% would hide the only case anybody needs to act on.
        UtilizationBands.Of(100m, 140m, hasCoverage: true).Should().Be(UtilizationBand.Exhausted);
        UtilizationBands.PercentUsed(100m, 140m).Should().Be(140m);
    }

    [Fact]
    public void An_unlimited_benefit_is_its_own_band_and_has_no_percentage()
    {
        // Rendered as 0% it invites "plenty left" on something never metered; as 100% it flags an outlier that
        // does not exist. Both are worse than admitting there is no percentage.
        UtilizationBands.Of(0m, 0m, hasCoverage: true).Should().Be(UtilizationBand.Unlimited);
        UtilizationBands.PercentUsed(0m, 0m).Should().BeNull();
    }

    [Fact]
    public void No_coverage_at_all_is_not_the_same_as_unlimited()
    {
        UtilizationBands.Of(0m, 0m, hasCoverage: false).Should().Be(UtilizationBand.Zero);
    }

    [Theory]
    [InlineData(0, MemberCountBand.Empty)]
    [InlineData(1, MemberCountBand.Small)]
    [InlineData(49, MemberCountBand.Small)]
    [InlineData(50, MemberCountBand.Medium)]
    [InlineData(250, MemberCountBand.Large)]
    [InlineData(1000, MemberCountBand.VeryLarge)]
    public void Member_count_bands_separate_empty_from_small(int count, MemberCountBand expected) =>
        MemberCountBands.Of(count).Should().Be(expected);

    // ---- Sort allow-list ---------------------------------------------------------------------------------

    [Fact]
    public void A_known_sort_field_parses_with_its_direction()
    {
        SortRequest.TryParse("-percentused", MemberSortFields.Allowed, MemberSortFields.Default, out var sort)
            .Should().BeTrue();
        sort.Field.Should().Be("percentused");
        sort.Descending.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_sort_field_is_rejected_rather_than_silently_defaulted()
    {
        // Silently falling back would answer a question the caller did not ask, and they would read the first
        // page as the answer to the one they did.
        SortRequest.TryParse("salary", MemberSortFields.Allowed, MemberSortFields.Default, out var sort)
            .Should().BeFalse();
        sort.Field.Should().Be(MemberSortFields.Default);
    }

    [Fact]
    public void The_sort_allow_list_contains_no_field_that_is_not_on_the_view()
    {
        // The allow-list is a list of things that reach the database from a query string. It exists precisely
        // so that adding a column to a table does not make it sortable — and so nothing on it is a typo.
        MemberSortFields.Allowed.Should().OnlyContain(f => !f.Any(char.IsUpper));
        PolicySortFields.Allowed.Should().OnlyContain(f => !f.Any(char.IsUpper));
        MemberSortFields.Allowed.Should().Contain(MemberSortFields.Default);
        PolicySortFields.Allowed.Should().Contain(PolicySortFields.Default);
    }

    // ---- Paging ------------------------------------------------------------------------------------------

    [Fact]
    public void Page_size_is_capped_because_an_uncapped_page_is_an_unclassified_export()
    {
        PageRequest.Of(1, 100_000).PageSize.Should().Be(PageRequest.MaxPageSize);
        PageRequest.Of(0, null).Page.Should().Be(1);
        PageRequest.Of(-5, 0).PageSize.Should().Be(1);
        PageRequest.Of(3, 25).Skip.Should().Be(50);
    }

    // ---- Payer scope -------------------------------------------------------------------------------------

    [Fact]
    public void No_assignment_means_unrestricted_and_an_outage_means_restricted_to_nothing()
    {
        // The two ends of the model. An empty set read as "unrestricted" would make an admin-service outage
        // WIDEN everyone's access, so "could not ask" has its own value.
        var payer = Guid.NewGuid();
        PermittedPayers.Unrestricted.Allows(payer).Should().BeTrue();
        PermittedPayers.DenyAll.Allows(payer).Should().BeFalse();
        PermittedPayers.DenyAll.AllowsUnattributed.Should().BeFalse();
    }

    [Fact]
    public void A_restricted_caller_sees_their_payer_and_no_other()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var permitted = PermittedPayers.RestrictedTo([mine]);

        PayerScopeRules.Check(permitted, mine).Should().Be(PayerScopeOutcome.Allowed);
        PayerScopeRules.Check(permitted, theirs).Should().Be(PayerScopeOutcome.Denied);
    }

    [Fact]
    public void A_policy_with_no_payer_is_readable_only_by_an_unrestricted_caller()
    {
        // The pre-19.2 rows the 19.7 backfill retires. A restricted user asked for ONE payer's book of
        // business, and a row that might belong to any payer is not it.
        PayerScopeRules.Check(PermittedPayers.Unrestricted, null).Should().Be(PayerScopeOutcome.Allowed);
        PayerScopeRules.Check(PermittedPayers.RestrictedTo([Guid.NewGuid()]), null)
            .Should().Be(PayerScopeOutcome.Denied);
    }

    // ---- Role projection ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("finance", true)]
    [InlineData("claims_officer", true)]
    [InlineData("beneficiary_mgmt", true)]
    [InlineData("policy_admin", true)]
    [InlineData("network_team", true)]
    [InlineData("reception", false)]
    [InlineData("call_center", false)]
    [InlineData("nurse", false)]
    public void The_money_line_is_the_same_one_utilization_draws(string role, bool mayRead) =>
        AdministrativeProjection.MayReadAmounts([role]).Should().Be(mayRead);

    [Theory]
    [InlineData("reception")]
    [InlineData("call_center")]
    public void The_front_desk_does_not_receive_a_termination_reason(string role)
    {
        // "Deceased", "left the programme", "suspected misuse" — each is something a member should hear from
        // the person handling their case, not read off a search result at a busy counter.
        AdministrativeProjection.MayReadCase([role]).Should().BeFalse();
        AdministrativeProjection.MayReadContract([role]).Should().BeFalse();
    }

    [Fact]
    public void Reception_still_receives_everything_that_answers_is_this_person_covered()
    {
        var row = SampleMemberRow();
        var view = MemberQueryRowView.From(row, null, new DateOnly(2026, 7, 1),
            mayReadAmounts: false, mayReadContract: false, mayReadCase: false);

        view.MemberNo.Should().NotBeEmpty();
        view.Status.Should().Be("Active");
        view.PlanLabel.Should().Be("Standard");
        view.WaitingPeriodState.Should().Be("Served");
        view.UtilizationBand.Should().Be("High");
        // The PERCENTAGE survives; the pounds do not. "This member is at 85%" is operational; the ceiling in
        // money is commercial.
        view.PercentUsed.Should().Be(85m);
        view.TotalLimit.Should().BeNull();
        view.TotalConsumed.Should().BeNull();
        view.TerminationReason.Should().BeNull();
        view.PayerId.Should().BeNull();
    }

    [Fact]
    public void An_administrator_receives_the_amounts_and_the_remaining_balance()
    {
        var view = MemberQueryRowView.From(SampleMemberRow(), null, new DateOnly(2026, 7, 1),
            mayReadAmounts: true, mayReadContract: true, mayReadCase: true);

        view.TotalLimit.Should().Be(100m);
        view.TotalConsumed.Should().Be(85m);
        view.TotalRemaining.Should().Be(15m);
    }

    // ---- Coverage detail assembly ------------------------------------------------------------------------

    [Fact]
    public void A_category_shows_the_configured_ceiling_beside_the_members_own()
    {
        // The divergence a plan amendment creates. Showing only the rule tells a member they have cover they
        // have already spent; showing only the accumulator leaves "why is my ceiling 5 000" unanswerable.
        var rule = Rule(limit: 200m, waitingDays: 0);
        var coverage = Coverage(limit: 100m, consumed: 40m);

        var detail = CoverageDetailAssembler.Category("LAB", rule, coverage,
            enrolledFrom: new DateOnly(2026, 1, 1), asOf: new DateOnly(2026, 7, 1));

        detail.Limit.Should().Be(100m, "the member's generated coverage is what the accumulator measures against");
        detail.ConfiguredLimit.Should().Be(200m, "the plan version in force would grant more today");
        detail.LimitDiffersFromPlan.Should().BeTrue();
        detail.Consumed.Should().Be(40m);
        detail.Remaining.Should().Be(60m);
        detail.PercentUsed.Should().Be(40m);
    }

    [Fact]
    public void An_unlimited_category_reports_no_limit_and_no_percentage()
    {
        var detail = CoverageDetailAssembler.Category("CONSULT", Rule(limit: null, waitingDays: 0),
            Coverage(limit: null, consumed: 0m), new DateOnly(2026, 1, 1), new DateOnly(2026, 7, 1));

        detail.IsCovered.Should().BeTrue();
        detail.Limit.Should().BeNull();
        detail.Remaining.Should().BeNull();
        detail.PercentUsed.Should().BeNull();
        detail.LimitDiffersFromPlan.Should().BeFalse("an unlimited member on an unlimited rule is not a divergence");
    }

    [Fact]
    public void A_category_the_member_holds_but_the_plan_no_longer_configures_is_still_shown()
    {
        // The member's balance is still spendable. Dropping the row would make it invisible on the one page
        // that exists to say what they are entitled to.
        var detail = CoverageDetailAssembler.Category("IMAGING", rule: null, Coverage(50m, 10m),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 7, 1));

        detail.IsCovered.Should().BeTrue();
        detail.Limit.Should().Be(50m);
        detail.CostShareByTier.Should().BeEmpty("there is no rule in force to price it at");
    }

    [Fact]
    public void The_waiting_period_boundary_is_the_last_day_inside_it()
    {
        var rule = Rule(limit: 100m, waitingDays: 30);
        var enrolled = new DateOnly(2026, 1, 1);

        var serving = CoverageDetailAssembler.Category("LAB", rule, Coverage(100m, 0m), enrolled, new DateOnly(2026, 1, 30));
        serving.WaitingPeriodEndsOn.Should().Be(new DateOnly(2026, 1, 30));
        serving.WaitingPeriodState.Should().Be("Serving", "a service ON the boundary day is not yet payable");

        var served = CoverageDetailAssembler.Category("LAB", rule, Coverage(100m, 0m), enrolled, new DateOnly(2026, 1, 31));
        served.WaitingPeriodState.Should().Be("Served");
    }

    [Fact]
    public void The_cost_share_grid_carries_one_row_per_tier_with_the_resolved_preauth_rule()
    {
        var rule = Rule(limit: 1000m, waitingDays: 0);
        rule.RequiresPreauth = false;
        rule.Tiers =
        [
            new BenefitRuleTier
            {
                RuleTierId = Guid.NewGuid(), BenefitRuleId = rule.RuleId, NetworkTierId = Guid.NewGuid(),
                TierCode = "T1", IsCovered = true, CoinsurancePercent = 10m,
            },
            new BenefitRuleTier
            {
                RuleTierId = Guid.NewGuid(), BenefitRuleId = rule.RuleId, NetworkTierId = Guid.NewGuid(),
                TierCode = "OON", IsCovered = true, CoinsurancePercent = 40m,
                RequiresPreauthOverride = true, LimitMultiplier = 0.5m,
            },
        ];

        var detail = CoverageDetailAssembler.Category("LAB", rule, Coverage(1000m, 0m),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 7, 1));

        detail.CostShareByTier.Should().HaveCount(2);
        var t1 = detail.CostShareByTier.Single(t => t.TierCode == "T1");
        t1.CoinsurancePercent.Should().Be(10m);
        t1.RequiresPreauth.Should().BeFalse();
        t1.LimitAtTier.Should().Be(1000m);

        var oon = detail.CostShareByTier.Single(t => t.TierCode == "OON");
        oon.CoinsurancePercent.Should().Be(40m);
        oon.RequiresPreauth.Should().BeTrue("out-of-network commonly needs authorization for open-access care");
        oon.LimitAtTier.Should().Be(500m);
    }

    [Fact]
    public void An_unreadable_exclusion_list_yields_nothing_rather_than_taking_down_the_page()
    {
        CoverageDetailAssembler.ParseExclusions("[\"Z00\",\"Z01\"]").Should().Equal("Z00", "Z01");
        CoverageDetailAssembler.ParseExclusions("not json").Should().BeEmpty();
        CoverageDetailAssembler.ParseExclusions(null).Should().BeEmpty();
    }

    // ---- Minimum necessary, structurally -----------------------------------------------------------------

    [Fact]
    public void No_query_or_coverage_payload_has_anywhere_to_put_a_clinical_value()
    {
        // The same argument 19.4 made, extended to this surface: a filter has to be remembered, a missing field
        // cannot be forgotten. Finance, the Network Team and the front desk all read these payloads, and none
        // of them may receive a diagnosis. Note BODIES are the one clinical channel on this surface and are
        // governed by NoteVisibilityRules (19.3) rather than by absence — see the test below.
        string[] forbidden =
        [
            "diagnosis", "icd", "cpt", "loinc", "clinical", "prescription", "drug",
            "allergy", "symptom", "procedure", "vital",
        ];

        Type[] payloads =
        [
            typeof(PolicyQueryRowView), typeof(MemberQueryRowView), typeof(MembershipSummaryView),
            typeof(CoveredFamilyMemberView), typeof(EnrollmentHistoryView), typeof(DocumentSummaryView),
            typeof(MemberCoverageDetail), typeof(CategoryCoverageDetail), typeof(TierCostShare),
            typeof(CoverageChangeEntry),
        ];

        foreach (var type in payloads)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = property.Name.ToLowerInvariant();
                forbidden.Should().NotContain(
                    word => name.Contains(word, StringComparison.Ordinal),
                    $"{type.Name}.{property.Name} would carry clinical content onto an administrative surface");
            }
        }
    }

    [Fact]
    public void A_note_body_on_the_360_still_goes_through_the_19_3_visibility_rules()
    {
        // The 360 composes notes, and a note is the one place clinical content can legitimately reach this
        // surface. Finance must receive the note's EXISTENCE and not its body — omitting the note entirely
        // would make the record look empty and send an officer away believing nothing was written.
        var author = Guid.NewGuid();
        NoteVisibilityRules.MayReadBody(NoteVisibility.Clinical, ["finance"], Guid.NewGuid(), author)
            .Should().BeFalse();
        NoteVisibilityRules.MayReadBody(NoteVisibility.Clinical, ["doctor"], Guid.NewGuid(), author)
            .Should().BeTrue();
    }

    // ---- Fixtures ----------------------------------------------------------------------------------------

    private static Mersal.Policy.Infrastructure.MemberQueryRow SampleMemberRow() => new(
        Guid.NewGuid(), Guid.NewGuid(), "MEM-2026-000001", Guid.NewGuid(), Guid.NewGuid(), "Standard",
        null, Guid.NewGuid(), Relationship.Principal, EnrollmentStatus.Active,
        new DateOnly(2026, 1, 1), null, new DateOnly(2026, 1, 30), Guid.NewGuid(), "left the programme",
        100m, 85m, true);

    private static BenefitRule Rule(decimal? limit, int waitingDays) => new()
    {
        RuleId = Guid.NewGuid(), PlanVersionId = Guid.NewGuid(), BenefitCategoryId = Guid.NewGuid(),
        IsCovered = true,
        LimitType = limit is null ? null : Domain.LimitType.Annual,
        LimitValue = limit,
        ResetPeriod = ResetPeriod.Yearly, WaitingPeriodDays = waitingDays, Exclusions = "[]",
    };

    private static Coverage Coverage(decimal? limit, decimal consumed)
    {
        var coverage = new Coverage
        {
            CoverageId = Guid.NewGuid(), PolicyId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(),
            BenefitCategoryId = Guid.NewGuid(), EffectiveFrom = new DateOnly(2026, 1, 1),
            Status = CoverageStatus.Active,
        };
        if (limit is { } value)
        {
            coverage.Limits.Add(new CoverageLimit
            {
                CoverageLimitId = Guid.NewGuid(), CoverageId = coverage.CoverageId,
                LimitType = Domain.LimitType.Annual, LimitValue = value, ConsumedValue = consumed,
                CurrencyCode = "EGP", ResetPeriod = ResetPeriod.Yearly,
            });
        }
        return coverage;
    }
}
