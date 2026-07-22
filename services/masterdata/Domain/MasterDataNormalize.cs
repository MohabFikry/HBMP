using System.Globalization;
using System.Text;

namespace Mersal.MasterData.Domain;

/// <summary>Canonicalization + validation helpers used by both the loaders and the service.</summary>
public static class MasterDataNormalize
{
    /// <summary>Trim + upper-case an ICD-10 code (dotted format preserved, e.g. "e11.9" → "E11.9").</summary>
    public static string Icd(string raw) => raw.Trim().ToUpperInvariant();

    /// <summary>Trim + upper-case a CPT code.</summary>
    public static string Cpt(string raw) => raw.Trim().ToUpperInvariant();

    /// <summary>Trim + upper-case an ATC code.</summary>
    public static string Atc(string raw) => raw.Trim().ToUpperInvariant();

    /// <summary>
    /// ATC level from code length: A(1)=1, A10(3)=2, A10B(4)=3, A10BA(5)=4, A10BA02(7)=5.
    /// Returns 0 for an unrecognized length.
    /// </summary>
    public static int AtcLevel(string atcCode) => atcCode.Trim().Length switch
    {
        1 => 1, 3 => 2, 4 => 3, 5 => 4, 7 => 5, _ => 0,
    };

    /// <summary>All ancestor ATC codes of a full code by truncation (e.g. A10BA02 → A, A10, A10B, A10BA).</summary>
    public static IEnumerable<string> AtcAncestors(string atcCode)
    {
        var c = Atc(atcCode);
        foreach (var len in new[] { 1, 3, 4, 5 })
        {
            if (c.Length > len) yield return c[..len];
        }
    }

    /// <summary>
    /// A stable natural key for a drug from its commercial name: upper-case, collapse whitespace,
    /// strip most punctuation. Deterministic so re-loads dedupe on the same key.
    /// </summary>
    public static string DrugCode(string commercialName)
    {
        var sb = new StringBuilder(commercialName.Length);
        var lastSpace = false;
        foreach (var ch in commercialName.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch); lastSpace = false;
            }
            else if (!lastSpace && sb.Length > 0)
            {
                sb.Append('-'); lastSpace = true;
            }
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 128 ? s[..128] : s;
    }

    /// <summary>Parse a decimal price, tolerant of blanks/locale.</summary>
    public static decimal? Price(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}
