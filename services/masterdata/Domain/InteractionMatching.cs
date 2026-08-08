namespace Mersal.MasterData.Domain;

/// <summary>One interaction rule that fired, and the two products it fired between.</summary>
/// <param name="MatchedOn">
/// The two sides as the rule names them — "warfarin" and "M01A (all NSAIDs)". A prescriber shown
/// "interaction between Marevan and Brufen" learns nothing about WHY; naming the molecule and the class is
/// what makes the alert transferable to the next brand they reach for.
/// </param>
public sealed record InteractionHit(
    Guid DrugAId,
    Guid DrugBId,
    InteractionRule Rule,
    string MatchedOn);

/// <summary>
/// Matches prescribed products against the curated ingredient-level interaction rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the products are resolved first.</b> An interaction is a property of molecules. The old model
/// keyed pairs on two product uuids, so one clinical fact — warfarin interacts with NSAIDs — needed a row
/// for every pair of brands containing them. Resolving each product to its ingredient set and its ATC
/// ancestors turns that back into one rule.
/// </para>
/// <para>
/// <b>Combination products are the case that makes it necessary rather than merely tidy.</b> A co-amoxiclav
/// brand carries one ATC for the compound; only its ingredient rows say it contains amoxicillin. The same
/// resolution is what lets Gate 5 find the paracetamol hidden inside a cold-and-flu remedy.
/// </para>
/// <para>
/// Pure, and separated from the endpoint for the same reason the allergy matcher is: the dangerous failures
/// here are matching failures, and matching has to be testable without a database.
/// </para>
/// </remarks>
public static class InteractionMatcher
{
    /// <summary>
    /// Every rule that fires across the given products, deduplicated.
    /// </summary>
    /// <remarks>
    /// <b>Deduplicated by (rule, drug pair).</b> A combination product can satisfy one side of a rule
    /// through more than one of its ingredients — a compound containing both ibuprofen and codeine matches
    /// an NSAID class rule once via the molecule and once via the ATC chain. Reporting it twice would show
    /// the prescriber two identical warnings and read as two independent risks.
    /// </remarks>
    public static IReadOnlyList<InteractionHit> Match(
        IReadOnlyList<DrugComposition> drugs, IReadOnlyList<InteractionRule> activeRules)
    {
        ArgumentNullException.ThrowIfNull(drugs);
        ArgumentNullException.ThrowIfNull(activeRules);

        var hits = new Dictionary<(Guid, Guid, Guid), InteractionHit>();

        for (var i = 0; i < drugs.Count; i++)
        {
            for (var j = i + 1; j < drugs.Count; j++)
            {
                var a = drugs[i];
                var b = drugs[j];

                // The same product twice is not an interaction — it is a DUPLICATION, which is a different
                // check with a different message (Gate 5). Matching a rule here would tell a prescriber that
                // a medicine interacts with itself.
                if (a.DrugId == b.DrugId) continue;

                foreach (var rule in activeRules)
                {
                    // Unordered: the rule is stored once, in whichever direction it was authored, and must
                    // fire whichever way round the two medicines appear on the prescription.
                    (string Subject, string Object)? forward =
                        Side(a, rule.SubjectKind, rule.SubjectValue) is { } fa
                        && Side(b, rule.ObjectKind, rule.ObjectValue) is { } fb
                            ? (fa, fb)
                            : null;

                    (string Subject, string Object)? reverse =
                        forward is null
                        && Side(b, rule.SubjectKind, rule.SubjectValue) is { } ra
                        && Side(a, rule.ObjectKind, rule.ObjectValue) is { } rb
                            ? (ra, rb)
                            : null;

                    var matched = forward ?? reverse;
                    if (matched is null) continue;

                    var key = (rule.RuleId, a.DrugId, b.DrugId);
                    if (hits.ContainsKey(key)) continue;

                    hits[key] = new InteractionHit(
                        a.DrugId, b.DrugId, rule,
                        $"{matched.Value.Subject} with {matched.Value.Object}");
                }
            }
        }

        return [.. hits.Values];
    }

    /// <summary>
    /// How this product satisfies one side of a rule, or null if it does not.
    /// </summary>
    /// <remarks>
    /// An ATC side matches against the product's ancestor CHAIN, not its own code: a rule written at 'M01A'
    /// must fire for a product coded 'M01AE01'. That is the whole reason class-level rules survive new
    /// products entering the market.
    /// </remarks>
    private static string? Side(DrugComposition drug, RuleSubjectKind kind, string value) => kind switch
    {
        RuleSubjectKind.Ingredient =>
            drug.IngredientKeys.Contains(value) ? value : null,
        RuleSubjectKind.AtcClass =>
            drug.AtcChain.Contains(MasterDataNormalize.Atc(value)) ? $"ATC class {value}" : null,
        _ => null,
    };
}
