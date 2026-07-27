using FluentAssertions;
using Mersal.Money;

namespace Mersal.Money.Tests;

/// <summary>
/// Phase 19.1b — the member/payer split at a network tier.
///
/// This is the arithmetic a receptionist reads off an eligibility card and a claims officer settles against, so
/// the two invariants asserted throughout matter more than any single case: the member never pays more than the
/// allowed amount, and member + payer reconciles to it EXACTLY. A split that does not reconcile is how a
/// settlement batch stops matching the sum of its own lines.
/// </summary>
public class CostShareTests
{
    private static Money Egp(decimal amount) => Money.Egp(amount);

    private static void Reconciles(CostShareSplit s)
    {
        (s.MemberShare + s.PayerShare).Should().Be(s.AllowedAmount, "the split must account for every piastre");
        s.MemberShare.Amount.Should().BeLessThanOrEqualTo(s.AllowedAmount.Amount);
        s.MemberShare.Amount.Should().BeGreaterThanOrEqualTo(0m);
        s.PayerShare.Amount.Should().BeGreaterThanOrEqualTo(0m);
    }

    [Fact]
    public void A_percentage_copay_takes_that_share_of_the_bill()
    {
        // The ordinary in-network case: 10% of 1,000.
        var split = CostShareCalculator.Split(Egp(1000m), new TierCostShareTerms(IsCovered: true, CopayPercent: 10m));

        split.Copay.Should().Be(Egp(100m));
        split.MemberShare.Should().Be(Egp(100m));
        split.PayerShare.Should().Be(Egp(900m));
        Reconciles(split);
    }

    [Fact]
    public void An_out_of_network_tier_charges_its_own_higher_share()
    {
        // The whole reason tiers exist: the SAME service, priced differently by where it was delivered.
        var inNetwork = CostShareCalculator.Split(Egp(1000m), new TierCostShareTerms(true, CopayPercent: 10m));
        var outOfNetwork = CostShareCalculator.Split(Egp(1000m), new TierCostShareTerms(true, CopayPercent: 40m));

        inNetwork.MemberShare.Should().Be(Egp(100m));
        outOfNetwork.MemberShare.Should().Be(Egp(400m));
    }

    [Fact]
    public void A_tier_that_covers_nothing_leaves_the_member_paying_all_of_it()
    {
        // Not covered here is the ABSENCE of a benefit, not a 100% co-pay — so the breakdown stays empty
        // rather than reporting a co-pay the plan never agreed.
        var split = CostShareCalculator.Split(Egp(1000m), new TierCostShareTerms(IsCovered: false));

        split.MemberShare.Should().Be(Egp(1000m));
        split.PayerShare.Should().Be(Egp(0m));
        split.Copay.Should().Be(Egp(0m));
        split.Coinsurance.Should().Be(Egp(0m));
        Reconciles(split);
    }

    [Fact]
    public void A_fixed_copay_is_a_flat_amount_regardless_of_the_bill()
    {
        var small = CostShareCalculator.Split(Egp(200m), new TierCostShareTerms(true, CopayFixed: 50m));
        var large = CostShareCalculator.Split(Egp(5000m), new TierCostShareTerms(true, CopayFixed: 50m));

        small.MemberShare.Should().Be(Egp(50m));
        large.MemberShare.Should().Be(Egp(50m));
        Reconciles(small);
        Reconciles(large);
    }

    [Fact]
    public void A_fixed_copay_larger_than_the_bill_never_charges_more_than_the_bill()
    {
        // A misconfigured co-pay is a configuration error; charging it would make the member pay more than the
        // service cost, which is a defect no downstream clamp should have to catch.
        var split = CostShareCalculator.Split(Egp(30m), new TierCostShareTerms(true, CopayFixed: 50m));

        split.MemberShare.Should().Be(Egp(30m));
        split.PayerShare.Should().Be(Egp(0m));
        Reconciles(split);
    }

    [Fact]
    public void The_deductible_is_taken_before_any_percentage()
    {
        // THE ordering decision (ADR-0019). 1,000 with a 200 deductible and 10% co-pay:
        // deductible 200, then 10% of the remaining 800 = 80. Member 280, payer 720.
        var split = CostShareCalculator.Split(Egp(1000m),
            new TierCostShareTerms(true, CopayPercent: 10m, Deductible: 200m));

        split.DeductibleApplied.Should().Be(Egp(200m));
        split.Copay.Should().Be(Egp(80m));
        split.MemberShare.Should().Be(Egp(280m));
        Reconciles(split);

        // Taking the percentage first instead would charge 100 on top of the same 200 deductible — a
        // percentage of money the member is already paying in full. That is the double-count this order avoids.
        split.MemberShare.Should().NotBe(Egp(300m));
    }

    [Fact]
    public void Coinsurance_applies_to_what_is_left_after_deductible_and_copay()
    {
        // 1,000 − 100 deductible = 900; 10% co-pay = 90; 20% coinsurance of the remaining 810 = 162.
        var split = CostShareCalculator.Split(Egp(1000m),
            new TierCostShareTerms(true, CopayPercent: 10m, CoinsurancePercent: 20m, Deductible: 100m));

        split.DeductibleApplied.Should().Be(Egp(100m));
        split.Copay.Should().Be(Egp(90m));
        split.Coinsurance.Should().Be(Egp(162m));
        split.MemberShare.Should().Be(Egp(352m));
        Reconciles(split);
    }

    [Fact]
    public void A_deductible_larger_than_the_bill_absorbs_it_without_going_negative()
    {
        var split = CostShareCalculator.Split(Egp(150m),
            new TierCostShareTerms(true, CopayPercent: 10m, Deductible: 500m));

        split.DeductibleApplied.Should().Be(Egp(150m));
        split.Copay.Should().Be(Egp(0m), "there is nothing left to take a percentage of");
        split.MemberShare.Should().Be(Egp(150m));
        Reconciles(split);
    }

    [Fact]
    public void No_cost_share_at_all_means_the_payer_covers_the_whole_amount()
    {
        var split = CostShareCalculator.Split(Egp(1000m), new TierCostShareTerms(IsCovered: true));

        split.MemberShare.Should().Be(Egp(0m));
        split.PayerShare.Should().Be(Egp(1000m));
        Reconciles(split);
    }

    [Fact]
    public void A_zero_amount_splits_into_zeroes_rather_than_a_stray_copay()
    {
        var split = CostShareCalculator.Split(Egp(0m), new TierCostShareTerms(true, CopayFixed: 50m));

        split.MemberShare.Should().Be(Egp(0m));
        Reconciles(split);
    }

    [Theory]
    [InlineData(333.33, 33.33)]     // a third of a third
    [InlineData(0.01, 33.33)]       // a piastre
    [InlineData(99999.99, 7.5)]
    [InlineData(1.05, 50)]          // lands exactly on a rounding midpoint
    public void The_split_always_reconciles_to_the_allowed_amount(decimal amount, decimal percent)
    {
        // Rounding is where a split silently stops adding up. The payer share is computed as the RESIDUE for
        // this reason, so no rounding remainder can go unaccounted for.
        var split = CostShareCalculator.Split(Egp(amount),
            new TierCostShareTerms(true, CopayPercent: percent, CoinsurancePercent: percent, Deductible: 0.07m));

        Reconciles(split);
    }

    // ---- The two explicit fields that used to be silent defaults ------------------------------------------

    [Fact]
    public void A_waived_deductible_exempts_the_category_without_zeroing_the_plans_deductible()
    {
        // Primary care commonly waives it. Modelling this as "set the deductible to zero" would lose the
        // distinction between "this category is exempt" and "this plan has no deductible" — which survive a
        // plan amendment differently, and only one of them should follow the category.
        var terms = new TierCostShareTerms(true, CopayPercent: 10m, Deductible: 200m, DeductibleWaived: true);

        var split = CostShareCalculator.Split(Egp(1000m), terms);

        split.DeductibleApplied.Should().Be(Egp(0m));
        split.Copay.Should().Be(Egp(100m), "the co-pay percentage now applies to the whole amount");
        split.MemberShare.Should().Be(Egp(100m));
        Reconciles(split);

        // The same terms without the waiver charge the deductible AND a smaller co-pay — a different number,
        // which is why the field cannot be left implicit.
        CostShareCalculator.Split(Egp(1000m), terms with { DeductibleWaived = false })
            .MemberShare.Should().Be(Egp(280m));
    }

    [Fact]
    public void Copay_accrues_to_the_deductible_only_when_the_plan_says_it_does()
    {
        var terms = new TierCostShareTerms(true, CopayPercent: 10m, Deductible: 200m);

        var doesNotCount = CostShareCalculator.Split(Egp(1000m), terms);
        var counts = CostShareCalculator.Split(Egp(1000m), terms with { CopayCountsTowardDeductible = true });

        // Same money out of the member's pocket either way…
        doesNotCount.MemberShare.Should().Be(counts.MemberShare);
        // …but a different amount of progress toward the year's deductible, which changes what they pay NEXT.
        doesNotCount.AccruesToDeductible.Should().Be(Egp(200m), "the deductible applied, and nothing else");
        counts.AccruesToDeductible.Should().Be(Egp(280m), "plus the 80 co-pay");
    }

    [Fact]
    public void What_accrues_is_never_more_than_what_the_member_actually_paid()
    {
        // The accrual is a COMPONENT of the member share, not an addition to it. If it could exceed the share,
        // a member would be credited with deductible progress they never funded.
        var terms = new TierCostShareTerms(true, CopayPercent: 25m, CoinsurancePercent: 20m,
            Deductible: 150m, CopayCountsTowardDeductible: true);

        var split = CostShareCalculator.Split(Egp(2000m), terms);

        split.AccruesToDeductible.Amount.Should().BeLessThanOrEqualTo(split.MemberShare.Amount);
        split.Coinsurance.Amount.Should().BeGreaterThan(0m, "so the case is not vacuous");
    }

    [Fact]
    public void Nothing_accrues_at_a_tier_that_covers_nothing()
    {
        // This money bought no benefit, so it must not buy progress toward one.
        var split = CostShareCalculator.Split(Egp(1000m),
            new TierCostShareTerms(IsCovered: false, Deductible: 200m, CopayCountsTowardDeductible: true));

        split.MemberShare.Should().Be(Egp(1000m));
        split.AccruesToDeductible.Should().Be(Egp(0m));
    }

    [Fact]
    public void Member_share_is_the_same_number_the_full_split_reports()
    {
        // Claims takes the single number; eligibility shows the breakdown. They must not be able to diverge.
        var terms = new TierCostShareTerms(true, CopayPercent: 15m, CoinsurancePercent: 10m, Deductible: 75m);

        CostShareCalculator.MemberShare(Egp(1234.56m), terms)
            .Should().Be(CostShareCalculator.Split(Egp(1234.56m), terms).MemberShare);
    }
}
