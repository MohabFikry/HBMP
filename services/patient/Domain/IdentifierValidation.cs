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
