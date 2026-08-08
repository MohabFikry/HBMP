using FluentAssertions;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Tests;

/// <summary>
/// 28.10 — which dosing rule applies (design 44 §4).
///
/// <para>
/// The old shape was one maximum per drug, with no indication and no population, and its fetcher was a
/// hard-coded empty dictionary. Three dimensions were missing and each of them changes the number: the same
/// molecule is dosed differently for different conditions, mg/kg is the only correct paediatric calculation
/// in a population that skews paediatric, and routes differ.
/// </para>
/// </summary>
public class DosingRuleSelectorTests
{
    private static DrugComposition Drug(string? atc = null, params string[] ingredients) =>
        new(Guid.NewGuid(),
            ingredients.ToHashSet(StringComparer.Ordinal),
            atc is null ? new HashSet<string>(StringComparer.Ordinal)
                : MasterDataNormalize.AtcAncestors(atc).Append(atc).ToHashSet(StringComparer.Ordinal));

    private static DosingRule Rule(
        string ingredient, DosingPopulation population, string? indicationScope = null,
        decimal? maxDaily = 4000, bool weightBased = false, decimal? mgPerKgMax = null,
        bool cap = true, string? route = null) =>
        new()
        {
            RuleId = Guid.NewGuid(),
            SubjectKind = RuleSubjectKind.Ingredient, SubjectValue = ingredient,
            IndicationIcdScope = indicationScope, Population = population, Route = route,
            DoseUnit = "mg", MaxDaily = maxDaily,
            IsWeightBased = weightBased, MgPerKgMax = mgPerKgMax, WeightCappedAtAdultDose = cap,
            Citation = "BNF 87", Source = "test", IsActive = true,
        };

    private static Dictionary<string, IReadOnlyList<string>> Dx(params (string Code, string[] Ancestors)[] rows) =>
        rows.ToDictionary(r => r.Code, r => (IReadOnlyList<string>)r.Ancestors, StringComparer.Ordinal);

    [Fact]
    public void AN_ADULT_AND_A_CHILD_ON_THE_SAME_DRUG_GET_DIFFERENT_RULES()
    {
        // The acceptance criterion doc 44 §4 states. One ceiling per drug cannot express it at all.
        var paracetamol = Drug("N02BE01", "paracetamol");
        var rules = new[]
        {
            Rule("paracetamol", DosingPopulation.Adult, maxDaily: 4000),
            Rule("paracetamol", DosingPopulation.Child, maxDaily: 4000, weightBased: true, mgPerKgMax: 60),
        };

        var adult = DosingRuleSelector.Select(paracetamol, Dx(), DosingPopulation.Adult, null, rules);
        var child = DosingRuleSelector.Select(paracetamol, Dx(), DosingPopulation.Child, null, rules);

        adult!.IsWeightBased.Should().BeFalse();
        child!.IsWeightBased.Should().BeTrue();
        child.MgPerKgMax.Should().Be(60);
    }

    [Fact]
    public void AN_INDICATION_SCOPED_RULE_BEATS_THE_GENERAL_CEILING()
    {
        // Amoxicillin for otitis media takes a higher mg/kg than the general paediatric rule — exactly the
        // case a single per-drug maximum cannot express.
        var amoxicillin = Drug("J01CA04", "amoxicillin");
        var rules = new[]
        {
            Rule("amoxicillin", DosingPopulation.Child, weightBased: true, mgPerKgMax: 40),
            Rule("amoxicillin", DosingPopulation.Child, indicationScope: "H66", weightBased: true, mgPerKgMax: 90),
        };

        var otitis = DosingRuleSelector.Select(
            amoxicillin, Dx(("H66.9", ["H66", "H65-H75"])), DosingPopulation.Child, null, rules);
        var other = DosingRuleSelector.Select(
            amoxicillin, Dx(("J06.9", ["J06", "J00-J06"])), DosingPopulation.Child, null, rules);

        otitis!.MgPerKgMax.Should().Be(90, "the rule scoped to this indication is the more specific one");
        other!.MgPerKgMax.Should().Be(40, "no otitis media diagnosis, so the general rule applies");
    }

    [Fact]
    public void NO_RECORDED_AGE_SELECTS_NO_RULE_AT_ALL()
    {
        // The judgement that keeps a partial dose check safe. Defaulting an unknown age to Adult would apply
        // an adult ceiling to a four-year-old — not a conservative check, but the absence of one wearing its
        // clothes. The engine then reports the missing input by name.
        var paracetamol = Drug("N02BE01", "paracetamol");

        DosingRuleSelector.Select(
            paracetamol, Dx(), population: null, route: null,
            [Rule("paracetamol", DosingPopulation.Adult)])
            .Should().BeNull();
    }

    [Fact]
    public void A_WEIGHT_BASED_CEILING_IS_CAPPED_AT_THE_ADULT_MAXIMUM()
    {
        // A 60kg twelve-year-old computes to 3600mg on 60 mg/kg, and to 6000mg on 100. Reporting the second
        // as the recommended maximum would have the platform endorsing an overdose of its own arithmetic.
        var capped = Rule("paracetamol", DosingPopulation.Child, maxDaily: 4000, weightBased: true, mgPerKgMax: 100);
        var uncapped = Rule("paracetamol", DosingPopulation.Child, maxDaily: 4000, weightBased: true, mgPerKgMax: 100, cap: false);

        DosingRuleSelector.MaxDailyFor(capped, 60).Should().Be(4000);
        DosingRuleSelector.MaxDailyFor(uncapped, 60).Should().Be(6000);

        // Under the cap, the computed value stands.
        DosingRuleSelector.MaxDailyFor(capped, 20).Should().Be(2000);
    }

    [Fact]
    public void A_WEIGHT_BASED_RULE_WITH_NO_WEIGHT_YIELDS_NO_CEILING()
    {
        // Null, not a guess. The engine turns this into "weight is required for weight-based dosing" —
        // NotChecked naming the missing input, never Ok.
        var rule = Rule("paracetamol", DosingPopulation.Child, weightBased: true, mgPerKgMax: 60);

        DosingRuleSelector.MaxDailyFor(rule, null).Should().BeNull();
        DosingRuleSelector.MaxDailyFor(rule, 0).Should().BeNull();
    }

    [Fact]
    public void A_route_specific_rule_is_not_applied_to_a_different_route()
    {
        var amoxicillin = Drug("J01CA04", "amoxicillin");
        var rules = new[] { Rule("amoxicillin", DosingPopulation.Adult, route: "PO") };

        DosingRuleSelector.Select(amoxicillin, Dx(), DosingPopulation.Adult, "PO", rules).Should().NotBeNull();
        DosingRuleSelector.Select(amoxicillin, Dx(), DosingPopulation.Adult, "IV", rules).Should().BeNull();
    }

    [Fact]
    public void A_drug_with_no_authored_rule_selects_nothing()
    {
        // The common case, and the honest one: outside the curated subset the check says "no dosing rule
        // configured" and shows the manufacturer's labelled dosing as reference, explicitly not compared.
        DosingRuleSelector.Select(
            Drug("C07AB07", "bisoprolol"), Dx(), DosingPopulation.Adult, null,
            [Rule("paracetamol", DosingPopulation.Adult)])
            .Should().BeNull();
    }
}
