using System.Text.RegularExpressions;

namespace Mersal.Admin.Domain;

/// <summary>The outcome of linting a notification template. Ok ⇒ safe to save.</summary>
public sealed record LintResult(bool Ok, IReadOnlyList<string> Errors)
{
    public static LintResult Pass() => new(true, []);
    public static LintResult Fail(params string[] errors) => new(false, errors);
}

/// <summary>
/// Data-minimization + parity linter for bilingual notification templates (phase 8b.2, FR-NOT-005 /
/// 11-permission-matrix). Two guards: (1) both AR and EN bodies must be present and non-empty (AR/RTL parity — no
/// English-only outbound), and (2) a template bound to an OUTBOUND channel (SMS / email) must contain NO
/// clinical/PHI token in subject or body — a diagnosis/result/note field bound to an SMS body is rejected. In-app
/// notifications (inside the authenticated portal) are exempt from the PHI-in-body rule but still require parity.
/// </summary>
public static partial class TemplateLinter
{
    /// <summary>Tokens that must never appear in an outbound (SMS/email) template body (clinical payload guard).</summary>
    public static readonly IReadOnlySet<string> ForbiddenTokens =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "diagnosis", "diagnoses", "icd", "note", "notes", "clinicalnote", "result", "resultvalue", "labresult" };

    private static readonly HashSet<string> OutboundChannels =
        new(StringComparer.OrdinalIgnoreCase) { "sms", "email", "whatsapp" };

    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public static LintResult Lint(string channel, string subjectEn, string subjectAr, string bodyEn, string bodyAr)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var errors = new List<string>();

        // (1) AR/EN parity — both bodies authored.
        if (string.IsNullOrWhiteSpace(bodyEn)) errors.Add("body-en-required");
        if (string.IsNullOrWhiteSpace(bodyAr)) errors.Add("body-ar-required");

        // (2) PHI-in-outbound guard.
        if (OutboundChannels.Contains(channel))
        {
            foreach (var (label, text) in new[] { ("subject-en", subjectEn), ("subject-ar", subjectAr), ("body-en", bodyEn), ("body-ar", bodyAr) })
            {
                foreach (Match m in TokenRegex().Matches(text ?? ""))
                {
                    if (ForbiddenTokens.Contains(m.Groups[1].Value))
                        errors.Add($"phi-token-in-outbound:{label}:{m.Groups[1].Value.ToLowerInvariant()}");
                }
            }
        }

        return errors.Count == 0 ? LintResult.Pass() : new LintResult(false, errors);
    }
}
