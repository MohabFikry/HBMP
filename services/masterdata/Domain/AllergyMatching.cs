namespace Mersal.MasterData.Domain;

/// <summary>How a recorded allergy was found to conflict with a medicine. Ordered by directness.</summary>
/// <remarks>
/// Carried into the finding because "conflicts with a recorded allergy" is not actionable and
/// "amoxicillin is one of the molecules your penicillin allergy covers" is. The three are also weighted
/// differently: an exact molecule and an ATC class are the allergy itself, whereas a cross-reaction is an
/// inference whose strength varies and must be stated (design 44 §8).
/// </remarks>
public enum AllergyMatchKind
{
    /// <summary>The medicine contains a molecule the allergen maps to exactly.</summary>
    ExactIngredient,

    /// <summary>The medicine's ATC class (or an ancestor) is one the allergen covers.</summary>
    AtcScope,

    /// <summary>A curated cross-reactivity relationship, at a stated confidence.</summary>
    CrossReactivity,
}

/// <summary>One recorded allergen's verdict against one medicine.</summary>
/// <param name="MatchedOn">What matched, in the words shown to the prescriber.</param>
/// <param name="Confidence">Set only for a cross-reactivity match. Null means the match is the allergy itself.</param>
/// <param name="Statement">The clinical sentence for a cross-reaction — mechanism and what to do instead.</param>
public sealed record AllergyMatch(
    Guid AllergenId,
    string AllergenName,
    AllergyMatchKind Kind,
    string MatchedOn,
    CrossReactivityConfidence? Confidence = null,
    string? StatementEn = null,
    string? StatementAr = null,
    string? Citation = null);

/// <summary>What is known about one medicine's composition, for matching against.</summary>
/// <param name="IngredientKeys">Every molecule the product contains. A combination product has several.</param>
/// <param name="AtcChain">The product's ATC code and every ancestor of it.</param>
public sealed record DrugComposition(
    Guid DrugId,
    IReadOnlySet<string> IngredientKeys,
    IReadOnlySet<string> AtcChain)
{
    /// <summary>
    /// Whether anything is known about this medicine at all.
    /// </summary>
    /// <remarks>
    /// A product with neither a recorded active ingredient (4.7% of the catalogue) nor an ATC code (14.8%)
    /// cannot be compared with any allergy. That is a gap in the DRUG's data rather than in the allergen
    /// mapping, and the two must be reported separately or a prescriber cannot tell which needs fixing.
    /// </remarks>
    public bool IsResolvable => IngredientKeys.Count > 0 || AtcChain.Count > 0;
}

/// <summary>One recorded allergen with everything it maps to.</summary>
public sealed record AllergenMapping(
    Guid AllergenId,
    string Name,
    bool IsDrugMappable,
    IReadOnlySet<string> IngredientKeys,
    IReadOnlySet<string> AtcScopes,
    IReadOnlyList<CrossReactivityRule> CrossReactivity)
{
    /// <summary>
    /// Whether this allergen can be compared with a medicine at all.
    /// </summary>
    /// <remarks>
    /// A drug allergen with no molecules, no ATC scope and no cross-reactivity group is UNMAPPED — the
    /// catalogue holds nothing to compare. The allergy check must name it and report NotChecked rather than
    /// pass the line, which is the defect this whole phase exists for (design 44 §1.1).
    /// </remarks>
    public bool IsMapped =>
        IngredientKeys.Count > 0 || AtcScopes.Count > 0 || CrossReactivity.Count > 0;
}

/// <summary>A cross-reactivity group as the matcher needs it: its members, its confidence, its wording.</summary>
public sealed record CrossReactivityRule(
    string GroupCode,
    string NameEn,
    CrossReactivityConfidence Confidence,
    string StatementEn,
    string StatementAr,
    string Citation,
    IReadOnlySet<string> IngredientKeys,
    IReadOnlySet<string> AtcScopes);

/// <summary>
/// Decides whether a medicine conflicts with a beneficiary's recorded allergies.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> A single expression that asked whether an allergen CODE
/// (<c>ALG-PENICILLIN</c>) appeared in the drug's ATC ancestor chain (<c>J</c>, <c>J01</c>, <c>J01C</c>).
/// Those are disjoint code spaces, so it was a constant false for every patient and every medicine — and
/// the prescribing engine rendered the constant false as "no conflict with the recorded allergies".
/// </para>
/// <para>
/// <b>Pure, and separated from the endpoint on purpose.</b> Every dangerous failure of an allergy check is
/// a MATCHING failure, not a transport failure, so the matching has to be unit-testable without a database.
/// The absence of any such test is why the previous version shipped and stayed shipped.
/// </para>
/// </remarks>
public static class AllergyMatcher
{
    /// <summary>
    /// Every recorded allergen that conflicts with this medicine, and every one that could not be evaluated.
    /// </summary>
    /// <remarks>
    /// Returns the gaps alongside the hits deliberately. A caller given only the conflicts cannot tell
    /// "compared against all three allergies and found nothing" from "compared against none of them", and
    /// the platform's five-state model exists precisely to keep those apart.
    /// </remarks>
    public static AllergyScreenResult Screen(
        DrugComposition drug, IReadOnlyList<AllergenMapping> recorded)
    {
        ArgumentNullException.ThrowIfNull(drug);
        ArgumentNullException.ThrowIfNull(recorded);

        var matches = new List<AllergyMatch>();
        var unmapped = new List<string>();
        var screened = 0;

        foreach (var allergen in recorded)
        {
            // A food or environmental allergen is neither screened nor a gap — it is not a question about a
            // medicine. Counting it either way would misreport the coverage of the check.
            if (!allergen.IsDrugMappable) continue;

            if (!allergen.IsMapped)
            {
                unmapped.Add(allergen.Name);
                continue;
            }

            // The medicine itself is unresolvable: nothing to compare against. Reported by the caller as a
            // property of the DRUG, so it is not misattributed to the allergen mapping.
            if (!drug.IsResolvable) continue;

            screened++;

            var match = Match(drug, allergen);
            if (match is not null) matches.Add(match);
        }

        return new AllergyScreenResult(matches, unmapped, screened, drug.IsResolvable);
    }

    /// <summary>
    /// The strongest match for one allergen, or null.
    /// </summary>
    /// <remarks>
    /// Precedence is exact molecule → ATC scope → cross-reactivity, and it is a precedence rather than a
    /// collection because the prescriber needs the most direct true statement. "Contains amoxicillin, which
    /// your penicillin allergy covers" outranks "may cross-react with penicillins" when both hold, and
    /// showing both would make the weaker one look like an additional finding.
    /// </remarks>
    private static AllergyMatch? Match(DrugComposition drug, AllergenMapping allergen)
    {
        var exact = allergen.IngredientKeys.Where(drug.IngredientKeys.Contains)
            .OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        if (exact is not null)
        {
            return new AllergyMatch(
                allergen.AllergenId, allergen.Name, AllergyMatchKind.ExactIngredient,
                $"contains {exact}, which is covered by the recorded allergy to {allergen.Name}");
        }

        var scope = allergen.AtcScopes.Where(drug.AtcChain.Contains)
            .OrderByDescending(s => s.Length).FirstOrDefault();
        if (scope is not null)
        {
            return new AllergyMatch(
                allergen.AllergenId, allergen.Name, AllergyMatchKind.AtcScope,
                $"belongs to ATC class {scope}, which the recorded allergy to {allergen.Name} covers");
        }

        // Highest confidence first: an allergen may carry both a moderate side-chain relationship and a
        // low-confidence class-wide one, and reporting the weaker of the two would understate a real risk.
        foreach (var rule in allergen.CrossReactivity.OrderBy(r => r.Confidence))
        {
            var hitIngredient = rule.IngredientKeys.FirstOrDefault(drug.IngredientKeys.Contains);
            var hitScope = rule.AtcScopes.FirstOrDefault(drug.AtcChain.Contains);
            if (hitIngredient is null && hitScope is null) continue;

            var what = hitIngredient is not null ? hitIngredient : $"ATC class {hitScope}";
            return new AllergyMatch(
                allergen.AllergenId, allergen.Name, AllergyMatchKind.CrossReactivity,
                $"possible cross-reaction ({rule.Confidence.ToString().ToLowerInvariant()} confidence) "
                + $"between {what} and the recorded allergy to {allergen.Name}",
                rule.Confidence, rule.StatementEn, rule.StatementAr, rule.Citation);
        }

        return null;
    }
}

/// <summary>The outcome of screening one medicine against a beneficiary's recorded allergies.</summary>
/// <param name="UnmappedAllergens">
/// Drug allergens the catalogue holds no mapping for. These are why the check may not report Ok.
/// </param>
/// <param name="ScreenedAllergenCount">
/// How many recorded allergens were actually compared. Only a screen where this accounts for every
/// medicine-related allergy, with nothing unmapped, is entitled to report Ok.
/// </param>
/// <param name="DrugResolvable">
/// False when the medicine has neither a recorded active ingredient nor an ATC code, so nothing could be
/// compared against it — a gap in the drug's own data, not in the allergen mapping.
/// </param>
public sealed record AllergyScreenResult(
    IReadOnlyList<AllergyMatch> Matches,
    IReadOnlyList<string> UnmappedAllergens,
    int ScreenedAllergenCount,
    bool DrugResolvable)
{
    /// <summary>The match to report: the most direct one found across every recorded allergen.</summary>
    public AllergyMatch? Strongest => Matches.OrderBy(m => m.Kind).ThenBy(m => m.Confidence).FirstOrDefault();
}
