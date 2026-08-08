using Mersal.ClinicalCodes;

namespace Mersal.MasterData.Domain;

/// <summary>One contraindication rule that fired, and what it fired on.</summary>
/// <param name="MatchedOn">
/// The drug side and the condition side in words — "all NSAIDs, with the recorded diagnosis K27.9". A
/// prescriber shown only "contraindicated" cannot carry the reasoning to the next drug they reach for.
/// </param>
public sealed record ContraindicationHit(
    Guid DrugId,
    DrugDiseaseContraindication Rule,
    string MatchedOn);

/// <summary>
/// Matches prescribed products against conditions the patient actually has.
/// </summary>
/// <remarks>
/// <para>
/// Pure, like the allergy and interaction matchers, and for the same reason: the dangerous failures of a
/// clinical rule are MATCHING failures, and matching has to be testable without a database.
/// </para>
/// <para>
/// The condition side walks the ICD-10 hierarchy (28.7) rather than comparing codes: a rule written at
/// <c>K27</c> catches <c>K27.9</c>, <c>K27.4</c> and every other subcategory without enumerating them, and
/// keeps catching them when a coder is more precise than usual.
/// </para>
/// </remarks>
public static class ContraindicationMatcher
{
    /// <summary>
    /// Every active rule that fires for this medicine, given the patient's conditions.
    /// </summary>
    /// <param name="diagnosisAncestors">
    /// Each recorded diagnosis with its ancestor chain, from <c>masterdata.icd_ancestor</c>.
    /// </param>
    /// <param name="isPregnant">
    /// True only when the status is RECORDED as pregnant. Unknown must not count as pregnant — a rule that
    /// fired on every patient whose status nobody had asked about would be dismissed within a day, and the
    /// alert would stop meaning anything for the patients it was written for.
    /// </param>
    public static IReadOnlyList<ContraindicationHit> Match(
        DrugComposition drug,
        IReadOnlyDictionary<string, IReadOnlyList<string>> diagnosisAncestors,
        bool isPregnant,
        IReadOnlyList<DrugDiseaseContraindication> activeRules)
    {
        ArgumentNullException.ThrowIfNull(drug);
        ArgumentNullException.ThrowIfNull(diagnosisAncestors);
        ArgumentNullException.ThrowIfNull(activeRules);

        var hits = new List<ContraindicationHit>();

        foreach (var rule in activeRules)
        {
            var side = DrugSide(drug, rule);
            if (side is null) continue;

            if (string.Equals(rule.IcdScope, DrugDiseaseContraindication.PregnancyScope, StringComparison.Ordinal))
            {
                if (!isPregnant) continue;
                hits.Add(new ContraindicationHit(drug.DrugId, rule, $"{side}, and the patient is recorded as pregnant"));
                continue;
            }

            var matched = diagnosisAncestors.Keys.FirstOrDefault(d =>
                IcdCodes.IsDescendantOrSelf(d, diagnosisAncestors[d], rule.IcdScope));

            if (matched is not null)
            {
                hits.Add(new ContraindicationHit(
                    drug.DrugId, rule, $"{side}, with the recorded diagnosis {matched}"));
            }
        }

        return hits;
    }

    /// <summary>How this product satisfies the drug side of a rule, or null.</summary>
    private static string? DrugSide(DrugComposition drug, DrugDiseaseContraindication rule) => rule.SubjectKind switch
    {
        RuleSubjectKind.Ingredient =>
            drug.IngredientKeys.Contains(rule.SubjectValue) ? $"contains {rule.SubjectValue}" : null,
        // An ATC side matches the product's ancestor CHAIN, not its own code: a rule at 'M01A' must fire for
        // a product coded 'M01AE01', which is the whole reason class-level rules survive new products.
        RuleSubjectKind.AtcClass =>
            drug.AtcChain.Contains(MasterDataNormalize.Atc(rule.SubjectValue))
                ? $"is in ATC class {rule.SubjectValue}" : null,
        _ => null,
    };
}
