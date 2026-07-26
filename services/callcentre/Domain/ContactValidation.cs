using System.Text.RegularExpressions;

namespace Mersal.CallCentre.Domain;

/// <summary>Server-side contact validation (phase 15.4). A correction is only forwarded to patient-service when the
/// value is well-formed for its kind (phone/email) — an invalid value is rejected (422) before anything is
/// persisted. Kept pure so the endpoint and tests share one rule set.</summary>
public static partial class ContactValidation
{
    [GeneratedRegex(@"^\+?[0-9][0-9\s\-]{6,19}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    /// <summary>The contact kinds the call centre may edit. Address is free-text (only non-empty is required).</summary>
    public static readonly IReadOnlySet<string> EditableKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Phone", "Mobile", "Email", "Address",
    };

    /// <summary>True when <paramref name="value"/> is well-formed for <paramref name="kind"/>.</summary>
    public static bool IsValid(string? kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(kind)) return false;
        return kind.Trim().ToLowerInvariant() switch
        {
            "phone" or "mobile" => PhoneRegex().IsMatch(value.Trim()),
            "email" => EmailRegex().IsMatch(value.Trim()),
            "address" => value.Trim().Length >= 3,
            _ => false,
        };
    }
}
