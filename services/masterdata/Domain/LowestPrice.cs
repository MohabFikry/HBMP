using System.Globalization;

namespace Mersal.MasterData.Domain;

/// <summary>What the comparison needs from a drug row.</summary>
public sealed record PricedDrug(
    string DrugId, string? Ingredient, string? Strength, string? Form, decimal? PriceEgp, decimal? PackSize);

/// <summary>The verdict for one drug.</summary>
/// <param name="PricePerUnit">price ÷ pack size — THE comparison basis. Null when either is unknown, and a
/// null is never labelled.</param>
/// <param name="GroupKey">The equivalence group, or null when the drug cannot be grouped at all.</param>
public sealed record PriceLabel(string DrugId, bool IsLowestPrice, decimal? PricePerUnit, string? GroupKey);

/// <summary>
/// 29.7 — the lowest-price label (design 45 §7).
///
/// <para><b>Two corrections, and they matter more than the feature.</b></para>
///
/// <para><b>1. The group is ingredient + strength + dosage form.</b> "Same active ingredients" alone is not a
/// valid comparison group: a 500 mg tablet and a 250 mg/5 mL syrup share an ingredient and cannot be
/// price-compared. Grouped on ingredient alone the syrup's per-mL price makes every tablet look expensive,
/// and the label stops meaning anything.</para>
///
/// <para><b>2. The comparison is per PRESCRIBING UNIT, not per pack.</b> A 20-tablet pack at 100 EGP is MORE
/// expensive per tablet than a 30-tablet pack at 120 EGP — 5.00 against 4.00 — yet pack price alone labels
/// the first as cheaper. That would actively mislead a prescriber trying to save a beneficiary money, which
/// is the exact opposite of what the feature is for.</para>
///
/// <para><b>Derived, never authored.</b> Recomputed whenever prices load, with a <c>computed_at</c> so a
/// stale label is detectable. A hand-set flag goes stale the first time a price moves, and nothing says so.</para>
/// </summary>
public static class LowestPrice
{
    /// <summary>
    /// Label every drug that is cheapest per prescribing unit within its equivalence group.
    /// </summary>
    public static IReadOnlyList<PriceLabel> Compute(IEnumerable<PricedDrug> drugs)
    {
        ArgumentNullException.ThrowIfNull(drugs);

        var rows = drugs.Select(d => new
        {
            Drug = d,
            Key = GroupKey(d),
            // NULL where either input is missing. The temptation here is to fall back to the pack price when
            // pack_size is unknown — which is EXACTLY the comparison this class exists to reject. An absent
            // label says "not compared"; a wrong one says "cheapest".
            PerUnit = d.PriceEgp is > 0 && d.PackSize is > 0 ? d.PriceEgp / d.PackSize : null,
        }).ToList();

        // Cheapest per-unit price in each group, over the COMPARABLE members only. A group whose members all
        // lack pack data has no minimum and labels nothing.
        var minimums = rows
            .Where(r => r.Key is not null && r.PerUnit is not null)
            .GroupBy(r => r.Key!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(r => r.PerUnit!.Value), StringComparer.Ordinal);

        return rows.ConvertAll(r => new PriceLabel(
            r.Drug.DrugId,
            // TIES ALL RECEIVE THE LABEL — equality, not a pick. Choosing one arbitrarily would tell a
            // prescriber that a genuinely equal alternative costs more.
            IsLowestPrice: r.Key is not null && r.PerUnit is not null
                           && minimums.TryGetValue(r.Key, out var min) && r.PerUnit == min,
            PricePerUnit: r.PerUnit,
            GroupKey: r.Key));
    }

    /// <summary>
    /// The equivalence group: active ingredient + strength + dosage form, normalised.
    ///
    /// <para>Null when the ingredient is unknown. Grouping the unknowns TOGETHER would compare a nameless
    /// painkiller against a nameless insulin and label one of them cheapest; an ungrouped drug is simply not
    /// compared.</para>
    ///
    /// <para>Case- and whitespace-insensitive because the source data is not tidy: "Amoxicillin " and
    /// "amoxicillin" must be one group, or the label silently splits into groups of one and every drug
    /// becomes "cheapest".</para>
    /// </summary>
    public static string? GroupKey(PricedDrug drug)
    {
        ArgumentNullException.ThrowIfNull(drug);
        var ingredient = Normalise(drug.Ingredient);
        if (ingredient.Length == 0) return null;

        return string.Join('|', ingredient, Normalise(drug.Strength), Normalise(drug.Form));
    }

    /// <summary>Lower-case, trimmed, internal whitespace collapsed and removed around units — so "500 MG" and
    /// "500mg" are the same strength, which they are.</summary>
    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var lowered = value.Trim().ToLower(CultureInfo.InvariantCulture);
        return string.Concat(lowered.Where(c => !char.IsWhiteSpace(c)));
    }
}
