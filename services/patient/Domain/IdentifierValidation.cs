using System.Text.RegularExpressions;

namespace Mersal.Patient.Domain;

/// <summary>
/// Format validation + normalization for beneficiary identifiers (US-001: "any one supported
/// identifier with valid format"). Refugee populations carry heterogeneous documents, so rules are
/// permissive but non-empty + type-appropriate; the value is normalized before dedup so trivial
/// variants collide.
/// </summary>
public static partial class IdentifierValidation
{
    public static string Normalize(string value) => value.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);

    public static bool IsValid(IdentifierType type, string value, out string? error)
    {
        error = null;
        var v = value?.Trim() ?? "";
        if (v.Length == 0) { error = "identifier value is required"; return false; }

        var ok = type switch
        {
            // Egyptian National ID: 14 digits.
            IdentifierType.NationalID => NationalId().IsMatch(v),
            // Passport: 5–20 alphanumerics (heterogeneous issuers).
            IdentifierType.Passport => Passport().IsMatch(v),
            // UNHCR number: e.g. 123-45C67890 style; accept alphanumerics + dashes, 6–20.
            IdentifierType.UNHCRNo => AlnumDash(6, 20).IsMatch(v),
            IdentifierType.RefugeeID => AlnumDash(4, 30).IsMatch(v),
            // MemberNo is issued by us (MRS-M-YYYY-NNNNNN); not user-submitted at registration.
            IdentifierType.MemberNo => MemberNo().IsMatch(v),
            _ => false,
        };
        if (!ok) error = $"'{value}' is not a valid {type}";
        return ok;
    }

    [GeneratedRegex(@"^\d{14}$")] private static partial Regex NationalId();
    [GeneratedRegex(@"^[A-Za-z0-9]{5,20}$")] private static partial Regex Passport();
    [GeneratedRegex(@"^MRS-M-\d{4}-\d{6}$", RegexOptions.IgnoreCase)] private static partial Regex MemberNo();

    private static Regex AlnumDash(int min, int max) => new($@"^[A-Za-z0-9\-]{{{min},{max}}}$");
}

/// <summary>
/// Person-name and contact-value validation (QA P0-2: `&lt;script&gt;x&lt;/script&gt;` and `abcdefg` both
/// reached the register as a family name and a phone number).
///
/// Names use a Unicode-letter ALLOWLIST, not a markup denylist: this registry serves Arabic, Latin and
/// other scripts, and enumerating the dangerous characters is the approach that misses one. Letters, marks
/// (Arabic diacritics), spaces, hyphen, apostrophe and period cover real names; angle brackets do not
/// appear in any of them. The record is rendered today by React (which escapes) but tomorrow by PDF
/// exports, SMS templates and CSVs — the store is the last common gate.
/// </summary>
public static partial class PersonFieldValidation
{
    public static bool IsValidName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 100 && Name().IsMatch(value.Trim());

    /// <summary>
    /// Phone: optional leading +, then 8–15 digits (E.164 range), separators tolerated and stripped.
    /// Deliberately NOT Egyptian-mobile-only: this population carries foreign numbers, and a rule that
    /// rejects a reachable Sudanese number to enforce a local format loses the one way to reach someone.
    /// </summary>
    public static bool IsValidPhone(string value)
    {
        var v = (value ?? "").Trim().Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
        return Phone().IsMatch(v);
    }

    public static bool IsValidContact(ContactType type, string value) => type switch
    {
        ContactType.Phone => IsValidPhone(value),
        ContactType.Email => Email().IsMatch((value ?? "").Trim()),
        // Address / emergency-contact are free text; length-bound only.
        _ => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 300,
    };

    [GeneratedRegex(@"^[\p{L}\p{M}][\p{L}\p{M}'\-\. ]*$")] private static partial Regex Name();
    [GeneratedRegex(@"^\+?\d{8,15}$")] private static partial Regex Phone();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex Email();
}
