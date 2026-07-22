using System.Text.Json;

namespace Mersal.Audit.Client;

/// <summary>
/// Minimizes before/after snapshots so audit records carry WHAT changed (field classes) and
/// safe/coded values, never raw PHI values (19-audit-strategy.md; CLAUDE.md § Audit).
/// Callers classify each field; PHI/PII-class fields are recorded as a redaction marker, not the value.
/// </summary>
public static class AuditSnapshot
{
    /// <summary>Field classes whose raw values must never enter the audit store.</summary>
    public static readonly IReadOnlySet<string> SensitiveClasses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "phi", "pii", "diagnosis", "clinical", "financials" };

    public const string Redacted = "[redacted]";

    /// <summary>
    /// Build a minimized JSON snapshot from field name → (value, class) pairs. Sensitive-class
    /// fields are redacted; the set of touched field-classes is returned alongside.
    /// </summary>
    public static (string Json, IReadOnlyList<string> FieldClasses) Minimize(
        IReadOnlyDictionary<string, (object? Value, string Class)> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var classes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var (name, (value, klass)) in fields)
            {
                classes.Add(klass);
                if (SensitiveClasses.Contains(klass))
                {
                    w.WriteString(name, Redacted);
                }
                else
                {
                    w.WritePropertyName(name);
                    JsonSerializer.Serialize(w, value);
                }
            }
            w.WriteEndObject();
        }

        return (System.Text.Encoding.UTF8.GetString(stream.ToArray()), classes.ToArray());
    }
}
