using FluentAssertions;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Tests;

/// <summary>
/// 29.7 / design 45 §7 — the lowest-price label.
///
/// <para>"The two corrections here matter more than the feature." Both are asserted directly: the comparison
/// is PER PRESCRIBING UNIT, and the equivalence group is ingredient + strength + form rather than ingredient
/// alone.</para>
/// </summary>
public class LowestPriceTests
{
    private static PricedDrug Drug(
        string id, string ingredient, string strength, string form, decimal? price, decimal? packSize) =>
        new(id, ingredient, strength, form, price, packSize);

    [Fact]
    public void The_cheaper_PACK_is_not_the_cheaper_TABLET()
    {
        // Design 45 §7's own worked example, and the whole reason the feature is written this way: a 20-tablet
        // pack at 100 EGP is 5.00/tablet; a 30-tablet pack at 120 EGP is 4.00/tablet. Comparing pack prices
        // would label the 100 EGP box as cheaper — actively misleading a prescriber trying to save a
        // beneficiary money, which is the opposite of the feature's purpose.
        var small = Drug("A", "amoxicillin", "500mg", "capsule", price: 100m, packSize: 20m);
        var large = Drug("B", "amoxicillin", "500mg", "capsule", price: 120m, packSize: 30m);

        var labelled = LowestPrice.Compute([small, large]);

        labelled.Single(d => d.IsLowestPrice).DrugId.Should().Be("B");
        labelled.Single(d => d.DrugId == "A").PricePerUnit.Should().Be(5m);
        labelled.Single(d => d.DrugId == "B").PricePerUnit.Should().Be(4m);
    }

    [Fact]
    public void Ingredient_alone_is_not_a_group()
    {
        // "A 500 mg tablet and a 250 mg/5 mL syrup share an ingredient and cannot be price-compared." Grouped
        // on ingredient alone, the syrup's per-mL price would make every tablet look expensive and the label
        // would be meaningless.
        var tablet = Drug("T", "paracetamol", "500mg", "tablet", price: 20m, packSize: 20m);   // 1.00 / tablet
        var syrup = Drug("S", "paracetamol", "250mg/5ml", "syrup", price: 12m, packSize: 120m); // 0.10 / mL

        var labelled = LowestPrice.Compute([tablet, syrup]);

        // BOTH are lowest — in their own groups. Neither has been compared against the other.
        labelled.Should().OnlyContain(d => d.IsLowestPrice);
        labelled.Select(d => d.GroupKey).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void The_group_is_ingredient_and_strength_and_form()
    {
        // Same ingredient and form, DIFFERENT strength: different groups. A 500 mg and a 250 mg capsule are
        // not substitutes at the same price.
        var strong = Drug("A", "amoxicillin", "500mg", "capsule", 100m, 20m);
        var weak = Drug("B", "amoxicillin", "250mg", "capsule", 60m, 20m);

        LowestPrice.Compute([strong, weak]).Select(d => d.GroupKey).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Ties_all_receive_the_label()
    {
        // "Ties ALL receive the label." Picking one arbitrarily would tell a prescriber that a genuinely equal
        // alternative is more expensive.
        var a = Drug("A", "metformin", "500mg", "tablet", 30m, 30m);   // 1.00
        var b = Drug("B", "metformin", "500mg", "tablet", 60m, 60m);   // 1.00
        var c = Drug("C", "metformin", "500mg", "tablet", 45m, 30m);   // 1.50

        var labelled = LowestPrice.Compute([a, b, c]);

        labelled.Where(d => d.IsLowestPrice).Select(d => d.DrugId).Should().BeEquivalentTo("A", "B");
    }

    [Fact]
    public void A_drug_with_no_pack_size_is_never_labelled()
    {
        // THE dependency on 29.6, handled honestly. Without a pack size there is no per-unit price, and
        // falling back to the PACK price is precisely the error §7 exists to prevent. So the drug is simply
        // not labelled — an absent label says "not compared", where a wrong label would say "cheapest".
        var known = Drug("A", "metformin", "500mg", "tablet", 30m, 30m);
        var noPack = Drug("B", "metformin", "500mg", "tablet", 10m, packSize: null);

        var labelled = LowestPrice.Compute([known, noPack]);

        labelled.Single(d => d.DrugId == "B").IsLowestPrice.Should().BeFalse(
            "10 EGP looks cheapest by PACK price, which is the comparison that misleads");
        labelled.Single(d => d.DrugId == "B").PricePerUnit.Should().BeNull();
        labelled.Single(d => d.DrugId == "A").IsLowestPrice.Should().BeTrue();
    }

    [Fact]
    public void A_drug_with_no_price_is_never_labelled()
    {
        var priced = Drug("A", "metformin", "500mg", "tablet", 30m, 30m);
        var unpriced = Drug("B", "metformin", "500mg", "tablet", price: null, packSize: 30m);

        var labelled = LowestPrice.Compute([priced, unpriced]);

        labelled.Single(d => d.DrugId == "B").IsLowestPrice.Should().BeFalse();
    }

    [Fact]
    public void A_group_with_one_comparable_member_still_labels_it()
    {
        // A single product in its group IS the cheapest available option, and saying so is useful: it tells
        // the prescriber there is no cheaper equivalent to look for.
        LowestPrice.Compute([Drug("A", "insulin glargine", "100IU/ml", "pen", 400m, 300m)])
            .Single().IsLowestPrice.Should().BeTrue();
    }

    [Fact]
    public void A_group_where_nothing_is_comparable_labels_nothing()
    {
        var a = Drug("A", "metformin", "500mg", "tablet", price: null, packSize: null);
        var b = Drug("B", "metformin", "500mg", "tablet", price: 10m, packSize: null);

        LowestPrice.Compute([a, b]).Should().OnlyContain(d => !d.IsLowestPrice);
    }

    [Fact]
    public void The_grouping_key_is_case_and_whitespace_insensitive()
    {
        // Source data is not tidy. "Amoxicillin " and "amoxicillin" must be one group, or the label silently
        // splits into two groups of one and every drug becomes "cheapest".
        var a = Drug("A", "Amoxicillin ", "500 MG", "Capsule", 100m, 20m);
        var b = Drug("B", "amoxicillin", "500mg", "capsule", 120m, 30m);

        var labelled = LowestPrice.Compute([a, b]);

        labelled.Select(d => d.GroupKey).Distinct().Should().HaveCount(1);
        labelled.Single(d => d.IsLowestPrice).DrugId.Should().Be("B");
    }

    [Fact]
    public void A_drug_with_no_ingredient_is_not_grouped_with_every_other_unknown()
    {
        // Grouping unknowns together would compare a nameless painkiller against a nameless insulin and label
        // one of them cheapest. An ungrouped drug is simply not compared.
        var a = Drug("A", "", "500mg", "tablet", 10m, 10m);
        var b = Drug("B", null!, "500mg", "tablet", 90m, 10m);

        var labelled = LowestPrice.Compute([a, b]);

        labelled.Should().OnlyContain(d => !d.IsLowestPrice);
        labelled.Should().OnlyContain(d => d.GroupKey == null);
    }
}
