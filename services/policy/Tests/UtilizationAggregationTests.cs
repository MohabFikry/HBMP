using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.4 — the utilization arithmetic and the min-necessary projection (design 38 §4.3).
///
/// Two families of test live here, and they guard the two ways a utilization report goes wrong:
/// <list type="bullet">
/// <item><b>It lies with a number</b> — an unlimited benefit rendered as 0%, an unattributed movement counted
/// as in-network, a group total that does not equal its members.</item>
/// <item><b>It leaks</b> — a clinical field reaching Finance, or an amount reaching a role with no financial
/// entitlement.</item>
/// </list>
/// </summary>
public class UtilizationAggregationTests
{
    private static MemberUtilization Member(string no, decimal limit, decimal consumed, bool unlimited = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), no, Guid.NewGuid(), null, limit, consumed, unlimited);

    // ---- Percentages: the null cases are the interesting ones -------------------------------------------

    [Fact]
    public void An_unlimited_benefit_has_no_percentage_rather_than_zero_percent()
    {
        // 0% invites "plenty left" on something that was never metered; 100% flags an outlier that does not
        // exist. Both are worse than an honest dash.
        var unlimited = new CategoryAccumulator(
            "CONSULT", Guid.NewGuid(), null, null, null, 0m, "EGP", ResetPeriod.None, null, null);

        unlimited.PercentUsed.Should().BeNull();
        unlimited.Remaining.Should().BeNull();
        unlimited.IsUnlimited.Should().BeTrue();
    }

    [Fact]
    public void A_zero_limit_has_no_percentage_either()
    {
        // Dividing by it would throw; rendering 100% would claim a benefit was exhausted that never existed.
        Accumulator(limit: 0m, consumed: 0m).PercentUsed.Should().BeNull();
    }

    [Theory]
    [InlineData(100d, 25d, 25.0d)]
    [InlineData(100d, 0d, 0.0d)]
    [InlineData(3d, 1d, 33.3d)]
    [InlineData(100d, 100d, 100.0d)]
    public void Percent_used_is_consumed_over_limit(double limit, double consumed, double expected)
    {
        Accumulator((decimal)limit, (decimal)consumed).PercentUsed.Should().Be((decimal)expected);
    }

    [Fact]
    public void Over_consumption_is_reported_past_one_hundred_percent_not_capped()
    {
        // A limit REDUCED mid-period legitimately leaves consumed > limit (0003 keeps the accumulator truthful
        // rather than rejecting care that already happened). Being at 140% is exactly the fact a utilization
        // report exists to surface — capping it hides the only rows anybody needs to look at.
        Accumulator(limit: 100m, consumed: 140m).PercentUsed.Should().Be(140m);
    }

    [Fact]
    public void Remaining_never_goes_negative()
    {
        // A negative "remaining" on a screen reads as a data fault, not as an over-consumed benefit.
        Accumulator(limit: 100m, consumed: 140m).Remaining.Should().Be(0m);
    }

    // ---- Reset dates -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ResetPeriod.Monthly, 2026, 3, 15, 2026, 4, 1)]
    [InlineData(ResetPeriod.Quarterly, 2026, 3, 15, 2026, 4, 1)]
    [InlineData(ResetPeriod.Quarterly, 2026, 5, 15, 2026, 7, 1)]
    [InlineData(ResetPeriod.Yearly, 2026, 3, 15, 2027, 1, 1)]
    public void The_next_reset_is_the_next_period_boundary(
        ResetPeriod period, int y, int m, int d, int ey, int em, int ed)
    {
        UtilizationMath.NextResetOn(period, LimitType.Annual, new DateOnly(y, m, d))
            .Should().Be(new DateOnly(ey, em, ed));
    }

    [Fact]
    public void A_lifetime_limit_never_resets()
    {
        UtilizationMath.NextResetOn(ResetPeriod.Yearly, LimitType.Lifetime, new DateOnly(2026, 3, 15))
            .Should().BeNull("a lifetime benefit that came back every January would not be a lifetime benefit");
    }

    [Fact]
    public void A_limit_with_no_reset_period_never_resets()
    {
        UtilizationMath.NextResetOn(ResetPeriod.None, LimitType.Annual, new DateOnly(2026, 3, 15))
            .Should().BeNull();
    }

    // ---- Outliers ----------------------------------------------------------------------------------------

    [Fact]
    public void Outliers_are_members_at_or_above_the_threshold_highest_first()
    {
        var members = new[]
        {
            Member("MEM-1", 100m, 10m),    // 10%
            Member("MEM-2", 100m, 95m),    // 95%
            Member("MEM-3", 100m, 80m),    // 80% — AT the threshold, and therefore in
            Member("MEM-4", 100m, 120m),   // 120%
        };

        var outliers = UtilizationMath.Outliers(members, 80m);

        outliers.Select(o => o.MemberNo).Should().Equal("MEM-4", "MEM-2", "MEM-3");
    }

    [Fact]
    public void The_threshold_is_configurable_because_normal_differs_by_programme()
    {
        // A chronic-care cohort at 80% in June is normal; a general cohort at 80% in February is not. A fixed
        // threshold would make the feature useless for one of them.
        var members = new[] { Member("MEM-1", 100m, 55m), Member("MEM-2", 100m, 45m) };

        UtilizationMath.Outliers(members, 50m).Should().HaveCount(1);
        UtilizationMath.Outliers(members, 40m).Should().HaveCount(2);
        UtilizationMath.Outliers(members, 90m).Should().BeEmpty();
    }

    [Fact]
    public void A_member_with_unlimited_benefits_is_never_an_outlier()
    {
        // They have no percentage, so they are neither inside nor outside a percentage band. Treating them as
        // 0% would quietly exclude them; treating them as 100% would flood the list with false alarms.
        var members = new[] { Member("MEM-1", 0m, 500m, unlimited: true) };

        UtilizationMath.Outliers(members, 0m).Should().BeEmpty();
    }

    // ---- Distribution ------------------------------------------------------------------------------------

    [Fact]
    public void The_distribution_bands_each_member_exactly_once()
    {
        var members = new[]
        {
            Member("A", 100m, 10m), Member("B", 100m, 30m), Member("C", 100m, 60m),
            Member("D", 100m, 80m), Member("E", 100m, 130m), Member("F", 0m, 0m, unlimited: true),
        };

        var buckets = UtilizationMath.Distribution(members);

        buckets.Single(b => b.Label == "0–25%").MemberCount.Should().Be(1);
        buckets.Single(b => b.Label == "25–50%").MemberCount.Should().Be(1);
        buckets.Single(b => b.Label == "50–75%").MemberCount.Should().Be(1);
        buckets.Single(b => b.Label == "75–100%").MemberCount.Should().Be(1);
        buckets.Single(b => b.Label == "100%+").MemberCount.Should().Be(1);
        buckets.Single(b => b.Label == "Unlimited").MemberCount.Should().Be(1);
        buckets.Sum(b => b.MemberCount).Should().Be(members.Length, "no member is double-counted or dropped");
    }

    [Fact]
    public void Unlimited_members_do_not_distort_the_lowest_band()
    {
        // Folding them into 0–25% would read as "barely using their benefit", which is the opposite of what an
        // uncapped benefit means.
        var buckets = UtilizationMath.Distribution([Member("A", 0m, 900m, unlimited: true)]);

        buckets.Single(b => b.Label == "0–25%").MemberCount.Should().Be(0);
        buckets.Single(b => b.Label == "Unlimited").MemberCount.Should().Be(1);
    }

    // ---- Rolling up --------------------------------------------------------------------------------------

    [Fact]
    public void A_scope_total_is_the_sum_of_its_members_and_nothing_is_re_derived()
    {
        // An aggregate that recomputes its parts is an aggregate that can disagree with them — and the
        // disagreement surfaces as a member refused care their own report says they are entitled to.
        var members = new[] { Member("A", 100m, 40m), Member("B", 200m, 60m), Member("C", 50m, 0m) };

        var (limit, consumed, remaining, percent) = UtilizationMath.Roll(members);

        limit.Should().Be(350m);
        consumed.Should().Be(100m);
        remaining.Should().Be(250m);
        percent.Should().Be(28.6m);
    }

    [Fact]
    public void An_empty_scope_rolls_to_zero_with_no_percentage()
    {
        var (limit, consumed, remaining, percent) = UtilizationMath.Roll([]);

        limit.Should().Be(0m);
        consumed.Should().Be(0m);
        remaining.Should().Be(0m);
        percent.Should().BeNull("0/0 is not 0%; a group with no members has no consumption rate");
    }

    // ---- The tier split ----------------------------------------------------------------------------------

    [Fact]
    public void Movements_fold_into_one_bucket_per_tier()
    {
        var split = UtilizationMath.SplitByTier(
        [
            ("T1", false, 3m), ("T1", false, 2m), ("OON", true, 4m),
        ]);

        split.Should().HaveCount(2);
        split.Single(t => t.TierCode == "T1").NetQuantity.Should().Be(5m);
        split.Single(t => t.TierCode == "T1").EventCount.Should().Be(2);
        split.Single(t => t.TierCode == "OON").IsOutOfNetwork.Should().BeTrue();
    }

    [Fact]
    public void An_unattributed_movement_gets_its_own_bucket_and_is_never_counted_in_network()
    {
        // THE decision this feature turns on. Folding unknown attribution into in-network biases the error in
        // the direction that flatters the network, on the very number the network is judged by. An explicit
        // gap is something someone can close; a silently in-network one is a wrong answer nobody can see.
        var split = UtilizationMath.SplitByTier([("T1", false, 5m), (null, false, 7m)]);

        var unattributed = split.Single(t => t.TierCode == TierUtilization.UnattributedCode);
        unattributed.IsAttributed.Should().BeFalse();
        unattributed.IsOutOfNetwork.Should().BeFalse("unknown is not out-of-network — it is unknown");
        unattributed.NetQuantity.Should().Be(7m);
        split.Single(t => t.TierCode == "T1").NetQuantity.Should().Be(5m, "it did not absorb the unknown");
    }

    [Fact]
    public void Attributed_tiers_sort_ahead_of_the_unattributed_bucket_and_the_order_is_stable()
    {
        // A table that reshuffles between refreshes is one nobody trusts enough to act on.
        var first = UtilizationMath.SplitByTier([(null, false, 1m), ("T2", false, 1m), ("T1", false, 1m)]);
        var second = UtilizationMath.SplitByTier([("T1", false, 1m), (null, false, 1m), ("T2", false, 1m)]);

        first.Select(t => t.TierCode).Should().Equal("T1", "T2", TierUtilization.UnattributedCode);
        second.Select(t => t.TierCode).Should().Equal(first.Select(t => t.TierCode));
    }

    [Fact]
    public void A_reversal_reduces_the_tier_total()
    {
        // A voided fulfillment did not happen. Counting it would inflate every report a void appears in.
        UtilizationMath.SplitByTier([("T1", false, 5m), ("T1", false, -2m)])
            .Single().NetQuantity.Should().Be(3m);
    }

    // ---- Unavailable is not zero -------------------------------------------------------------------------

    [Fact]
    public void An_unreachable_source_reports_null_not_zero()
    {
        // A zero is indistinguishable from "this member used nothing", and the two lead to opposite decisions:
        // one is a healthy member, the other is a broken report.
        var unavailable = ExternalUtilization.Unavailable;

        unavailable.EncounterCount.Should().BeNull();
        unavailable.ClaimedAmount.Should().BeNull();
        unavailable.AuthorizationsRaised.Should().BeNull();
        unavailable.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void The_response_names_which_sources_did_not_answer()
    {
        // Someone comparing two groups has to know one of them is missing its claim value, and the report is
        // the only place they will reliably see that.
        var facts = new UtilizationFacts(ExternalUtilization.Unavailable, ["claims-service"]);
        var view = ExternalUtilizationView.From(facts);

        view.Unavailable.Should().Contain("claims-service");
        view.ClaimedAmount.Should().BeNull();
    }

    // ---- Min-necessary: the projection matrix -----------------------------------------------------------

    [Theory]
    [InlineData("finance")]
    [InlineData("claims_officer")]
    [InlineData("beneficiary_mgmt")]
    [InlineData("policy_admin")]
    [InlineData("network_team")]
    public void Financial_roles_read_the_amounts(string role)
    {
        UtilizationProjection.Project(Full(), [role]).ClaimedAmount.Should().Be(1_500m);
    }

    [Theory]
    [InlineData("reception")]
    [InlineData("call_center")]
    [InlineData("lab_tech")]
    [InlineData("nurse")]
    public void Non_financial_roles_get_the_counts_but_not_the_money(string role)
    {
        // The counts are operational — "has this member been seen" is a question reception legitimately asks.
        // The amounts are `financials`, and stop at the same line a Financial note stops at (19.3).
        var projected = UtilizationProjection.Project(Full(), [role]);

        projected.ClaimedAmount.Should().BeNull();
        projected.ApprovedAmount.Should().BeNull();
        projected.MemberShareAmount.Should().BeNull();
        projected.Encounters.Should().Be(4, "the count is not the money");
        projected.AuthorizationsRaised.Should().Be(3);
        JsonSerializer.Serialize(projected).Should().NotContain("1500");
    }

    // ---- Min-necessary: structural absence of clinical content ------------------------------------------

    [Fact]
    public void No_utilization_type_carries_a_clinical_field_for_any_role()
    {
        // The strongest form of the rule: rather than stripping clinical values per role, the payload has
        // nowhere to put one. A filter has to be remembered; a missing field cannot be forgotten.
        string[] forbidden =
        [
            "diagnosis", "icd", "cpt", "loinc", "note", "clinical", "prescription", "drug",
            "allergy", "result", "symptom", "procedure",
        ];

        Type[] payloadTypes =
        [
            typeof(MemberUtilizationView), typeof(ScopeUtilizationView), typeof(CategoryUtilizationView),
            typeof(TierUtilizationView), typeof(MemberRowView), typeof(ExternalUtilizationView),
            typeof(DistributionBucketView), typeof(ReconciliationView),
        ];

        foreach (var type in payloadTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var word in forbidden)
                {
                    property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                        .Should().BeFalse($"{type.Name}.{property.Name} must not carry clinical content");
                }
            }
        }
    }

    // ---- Reconciliation is stated in the response -------------------------------------------------------

    [Fact]
    public void A_matching_pair_reconciles()
    {
        ReconciliationView.Of(500m, 500m).Reconciled.Should().BeTrue();
    }

    [Fact]
    public void A_mismatch_is_visible_on_the_report_rather_than_only_in_a_test()
    {
        // A report is read on days no test runs. If the two paths ever disagree, whoever is about to act on
        // the number must see it — not discover it afterwards from the audit trail.
        var view = ReconciliationView.Of(500m, 480m);

        view.Reconciled.Should().BeFalse();
        view.AccumulatorTotal.Should().Be(500m);
        view.ReportedTotal.Should().Be(480m);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private static CategoryAccumulator Accumulator(decimal limit, decimal consumed) =>
        new("LAB", Guid.NewGuid(), Guid.NewGuid(), LimitType.Annual, limit, consumed, "EGP",
            ResetPeriod.Yearly, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));

    private static ExternalUtilizationView Full() =>
        new(4, 3, 2, 1, 1_500m, 1_200m, 300m, "EGP", []);
}
