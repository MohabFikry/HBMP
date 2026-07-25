using System.Text.RegularExpressions;

namespace Mersal.Notification.Domain;

/// <summary>Renders a bilingual template by interpolating <c>{token}</c> placeholders from a min-necessary,
/// non-clinical field bag. Both AR and EN bodies are pre-authored (never machine-translated at send time); this only
/// substitutes tokens. A missing token renders empty (never leaks the raw brace or an object). The field bag must
/// carry ONLY non-clinical values — the caller (dispatcher) is responsible for never placing a diagnosis/clinical
/// note into it; <see cref="ForbiddenKeys"/> is a defensive backstop asserted in tests.</summary>
public static partial class TemplateRenderer
{
    /// <summary>Keys that must never appear in a notification field bag (clinical payload guard, 11-permission-matrix).</summary>
    public static readonly IReadOnlySet<string> ForbiddenKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "diagnosis", "diagnoses", "icd", "note", "notes", "clinicalNote", "result", "resultValue" };

    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public static string Render(string template, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(fields);
        return TokenRegex().Replace(template, m =>
            fields.TryGetValue(m.Groups[1].Value, out var v) ? v : string.Empty);
    }

    /// <summary>True if the field bag carries any forbidden (clinical) key — a bug in the caller, not user input.</summary>
    public static bool ContainsClinicalField(IReadOnlyDictionary<string, string> fields) =>
        fields.Keys.Any(ForbiddenKeys.Contains);
}
