using System.Globalization;
using System.Text.RegularExpressions;

namespace Mersal.Prescribing;

/// <summary>
/// 31.3 — the measurements a drug catalogue writes as prose, read as numbers.
///
/// <para><b>Why parsing at all.</b> "Volume / Weight" and "Strength" are free text — "120 ml", "1.5 ml",
/// "30 gm", "100 iu/ml", "300 I.U./ML" — and they carry the only fact that says how much medicine is inside
/// a container. Without them a box of syrup is a box of ONE and a course of 210 ml reads as 210 bottles.</para>
///
/// <para><b>What it deliberately refuses to read.</b> A TOTAL is not a concentration. "50000 iu" on a vitamin
/// D capsule is what the capsule holds; multiplied by a volume it becomes a hundred times the course. Only an
/// explicit per-millilitre concentration is taken as one, and everything else returns null — which the
/// callers carry through as "not derivable" rather than as a default.</para>
/// </summary>
public static class PackMeasure
{
    // Anchored on the UNIT, not on position: the number may be anywhere in the cell ("75mg/3ml 6 amp").
    private static readonly Regex Ml = new(
        @"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>millilitres?|milliliters?|ml|cc)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Litre = new(
        @"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>litres?|liters?|l)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Gm = new(
        @"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>grams?|gms?|gm|g)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Kg = new(
        @"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>kilograms?|kgs?|kg)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A concentration in international units per millilitre.
    /// </summary>
    /// <remarks>
    /// The "/ml" is REQUIRED. The catalogue writes the unit half a dozen ways — <c>iu/ml</c>, <c>i.u./ml</c>,
    /// <c>I.U./ML</c>, with and without a space — and all of them mean the same thing; but <c>50000 iu</c>
    /// without it means something else entirely, and the two live in the same column.
    /// </remarks>
    private static readonly Regex IuPerMl = new(
        @"(?<n>\d+(?:[.,]\d+)?)\s*i\.?\s*u\.?\s*/\s*ml\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Millilitres, converting a litre figure. Null when the text states no volume.</summary>
    public static decimal? Millilitres(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Millilitres FIRST: "ml" also ends in "l", so a lone litre pattern would match "120 ml" as 120 L.
        if (Match(Ml, text) is { } ml) return ml;
        return Match(Litre, text) is { } litres ? litres * 1000m : null;
    }

    /// <summary>Grams, converting a kilogram figure. Null when the text states no weight.</summary>
    public static decimal? Grams(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Kilograms first, for the same reason: "1 kg" contains a "g".
        if (Match(Kg, text) is { } kg) return kg * 1000m;
        return Match(Gm, text);
    }

    /// <summary>International units per millilitre, or null where the text states a total rather than a
    /// concentration.</summary>
    public static decimal? IuPerMillilitre(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Match(IuPerMl, text);

    private static decimal? Match(Regex re, string text)
    {
        var m = re.Match(text);
        if (!m.Success) return null;
        var n = m.Groups["n"].Value.Replace(",", ".");
        return decimal.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : null;
    }
}
