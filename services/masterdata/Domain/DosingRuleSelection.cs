using Mersal.ClinicalCodes;

namespace Mersal.MasterData.Domain;

/// <summary>
/// Picks the ONE dosing rule that applies, out of everything authored for a medicine.
/// </summary>
/// <remarks>
/// <para>
/// Selection is <b>most specific first</b> (design 44 §4). A rule scoped to an indication beats the general
/// "any indication" ceiling; a rule naming the patient's population beats one that does not; a rule naming
/// the route beats one that does not.
/// </para>
/// <para>
/// The alternative — evaluating every matching rule and taking the strictest — sounds safer and is not. It
/// would apply a paediatric mg/kg ceiling to an adult, and an otitis-media ceiling to a patient being
/// treated for something else, and the prescriber would be shown a range that belongs to a different
/// patient. A recommended range is only useful if it is the RIGHT one.
/// </para>
/// <para>
/// Pure, and tested without a database, for the same reason as the other matchers in this phase.
/// </para>
/// </remarks>
public static class DosingRuleSelector
{
    /// <summary>
    /// The applicable rule, or null when nothing was authored for this medicine and patient.
    /// </summary>
    /// <param name="population">
    /// The patient's band. Null when no age is recorded — a rule written for a specific population is then
    /// NOT selected, because applying an adult ceiling to a patient of unknown age is the assumption this
    /// phase exists to stop.
    /// </param>
    public static DosingRule? Select(
        DrugComposition drug,
        IReadOnlyDictionary<string, IReadOnlyList<string>> diagnosisAncestors,
        DosingPopulation? population,
        string? route,
        IReadOnlyList<DosingRule> activeRules)
    {
        ArgumentNullException.ThrowIfNull(drug);
        ArgumentNullException.ThrowIfNull(diagnosisAncestors);
        ArgumentNullException.ThrowIfNull(activeRules);

        var candidates = activeRules
            .Where(r => Applies(drug, diagnosisAncestors, population, route, r))
            .ToList();

        if (candidates.Count == 0) return null;

        // Specificity, in the order the design states: indication scope, then population, then route. Ties
        // resolve to the more specific indication scope — a rule at a subcategory beats one at a category.
        return candidates
            .OrderByDescending(r => r.IndicationIcdScope is not null)
            .ThenByDescending(r => r.IndicationIcdScope?.Length ?? 0)
            .ThenByDescending(r => r.Route is not null)
            .ThenByDescending(r => r.SubjectKind == RuleSubjectKind.Ingredient)
            .First();
    }

    private static bool Applies(
        DrugComposition drug,
        IReadOnlyDictionary<string, IReadOnlyList<string>> diagnosisAncestors,
        DosingPopulation? population,
        string? route,
        DosingRule rule)
    {
        var drugMatches = rule.SubjectKind switch
        {
            RuleSubjectKind.Ingredient => drug.IngredientKeys.Contains(rule.SubjectValue),
            RuleSubjectKind.AtcClass => drug.AtcChain.Contains(MasterDataNormalize.Atc(rule.SubjectValue)),
            _ => false,
        };
        if (!drugMatches) return false;

        // No recorded age means no population-specific rule may be chosen. An adult ceiling silently applied
        // to a four-year-old is not a conservative check; it is the absence of one wearing its clothes.
        if (population is null || rule.Population != population) return false;

        // A route on the rule must match the prescribed route; a rule with no route applies to any.
        if (rule.Route is not null && !string.Equals(rule.Route, route, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // An indication-scoped rule applies only when the patient actually has a condition underneath it.
        if (rule.IndicationIcdScope is { } scope)
        {
            return diagnosisAncestors.Keys.Any(d =>
                IcdCodes.IsDescendantOrSelf(d, diagnosisAncestors[d], scope));
        }

        return true;
    }

    /// <summary>
    /// The maximum daily dose this rule implies for a patient of the given weight.
    /// </summary>
    /// <remarks>
    /// For a weight-based rule this is mg/kg × weight, <b>capped at the adult maximum</b> where the rule says
    /// so — a 60kg adolescent computes past the adult ceiling on paracetamol, and reporting that as the
    /// recommended maximum would have the platform endorsing an overdose of its own arithmetic.
    /// </remarks>
    public static decimal? MaxDailyFor(DosingRule rule, decimal? weightKg)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.IsWeightBased) return rule.MaxDaily;
        if (weightKg is not > 0 || rule.MgPerKgMax is not { } perKg) return null;

        var computed = perKg * weightKg.Value;
        return rule.WeightCappedAtAdultDose && rule.MaxDaily is { } cap && computed > cap ? cap : computed;
    }
}
