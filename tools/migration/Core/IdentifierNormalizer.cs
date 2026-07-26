using System.Globalization;

namespace Mersal.Migration.Core;

public enum IdentifierKind { NationalId, Unhcr, Passport, Unknown }

/// <summary>A normalized beneficiary identifier + whether it validates and, if not, why.</summary>
public sealed record NormalizedIdentifier(IdentifierKind Kind, string Value, bool IsValid, string? Reason)
{
    /// <summary>The canonical de-dupe key: kind + normalized value (stable across source formatting).</summary>
    public string Key => $"{Kind}:{Value}";
}

/// <summary>
/// Normalizes the identifier formats a refugee-health migration meets — Egyptian national ID,
/// UNHCR/ProGres registration numbers, and travel-document/passport numbers — to a canonical form
/// so dedupe and idempotent upsert key off one stable value regardless of source formatting
/// (spaces, dashes, Arabic-Indic digits, case). Pure + deterministic (phase 12.1 STREAM C).
/// </summary>
public static class IdentifierNormalizer
{
    /// <summary>Normalize, auto-detecting the kind from shape when <paramref name="kindHint"/> is Unknown.</summary>
    public static NormalizedIdentifier Normalize(string? raw, IdentifierKind kindHint = IdentifierKind.Unknown)
    {
        var cleaned = Clean(raw);
        if (cleaned.Length == 0)
            return new NormalizedIdentifier(kindHint, string.Empty, false, "empty");

        var kind = kindHint == IdentifierKind.Unknown ? Detect(cleaned) : kindHint;
        return kind switch
        {
            IdentifierKind.NationalId => NationalId(cleaned),
            IdentifierKind.Unhcr => Unhcr(cleaned),
            IdentifierKind.Passport => Passport(cleaned),
            _ => new NormalizedIdentifier(IdentifierKind.Unknown, cleaned, false, "unrecognized format"),
        };
    }

    /// <summary>Fold Arabic-Indic digits to ASCII, uppercase, and drop spaces/dashes/dots.</summary>
    private static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var chars = new List<char>(raw.Length);
        foreach (var c in raw.Trim().ToUpperInvariant())
        {
            if (c is ' ' or '-' or '.' or '/' or '_') continue;
            // Arabic-Indic (٠..٩ = U+0660..0669) and Extended (۰..۹ = U+06F0..06F9) → ASCII.
            if (c is >= '٠' and <= '٩') chars.Add((char)('0' + (c - '٠')));
            else if (c is >= '۰' and <= '۹') chars.Add((char)('0' + (c - '۰')));
            else chars.Add(c);
        }
        return new string(chars.ToArray());
    }

    private static IdentifierKind Detect(string cleaned)
    {
        if (cleaned.Length == 14 && cleaned.All(char.IsAsciiDigit)) return IdentifierKind.NationalId;
        if (cleaned.Contains('C', StringComparison.Ordinal) && cleaned.Any(char.IsAsciiDigit)) return IdentifierKind.Unhcr;
        if (cleaned.Length is >= 6 and <= 9 && cleaned.All(char.IsAsciiLetterOrDigit)) return IdentifierKind.Passport;
        return IdentifierKind.Unknown;
    }

    /// <summary>Egyptian national ID: 14 digits, century digit + YYMMDD + governorate + serial + check.</summary>
    private static NormalizedIdentifier NationalId(string v)
    {
        if (v.Length != 14 || !v.All(char.IsAsciiDigit))
            return new NormalizedIdentifier(IdentifierKind.NationalId, v, false, "must be 14 digits");

        var century = v[0] switch { '2' => 1900, '3' => 2000, _ => -1 };
        if (century < 0)
            return new NormalizedIdentifier(IdentifierKind.NationalId, v, false, "invalid century digit");

        var year = century + int.Parse(v.AsSpan(1, 2), CultureInfo.InvariantCulture);
        var month = int.Parse(v.AsSpan(3, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(v.AsSpan(5, 2), CultureInfo.InvariantCulture);
        if (!IsRealDate(year, month, day))
            return new NormalizedIdentifier(IdentifierKind.NationalId, v, false, "invalid birth date encoded");

        return new NormalizedIdentifier(IdentifierKind.NationalId, v, true, null);
    }

    /// <summary>UNHCR/ProGres number: digits with a single 'C' case marker, e.g. 776-01C01234 → 77601C01234.</summary>
    private static NormalizedIdentifier Unhcr(string v)
    {
        var cCount = v.Count(c => c == 'C');
        var rest = v.Where(c => c != 'C').ToArray();
        var valid = cCount == 1 && rest.Length is >= 6 and <= 12 && rest.All(char.IsAsciiDigit);
        return new NormalizedIdentifier(IdentifierKind.Unhcr, v, valid,
            valid ? null : "expected digits with a single 'C' case marker");
    }

    /// <summary>Travel-document/passport: 6–9 uppercase alphanumerics.</summary>
    private static NormalizedIdentifier Passport(string v)
    {
        var valid = v.Length is >= 6 and <= 9 && v.All(char.IsAsciiLetterOrDigit) && v.Any(char.IsAsciiDigit);
        return new NormalizedIdentifier(IdentifierKind.Passport, v, valid,
            valid ? null : "expected 6-9 alphanumerics");
    }

    private static bool IsRealDate(int year, int month, int day)
    {
        if (year is < 1900 or > 2099 || month is < 1 or > 12 || day < 1) return false;
        return day <= DateTime.DaysInMonth(year, month);
    }
}
