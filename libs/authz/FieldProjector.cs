using Mersal.Audit.Client;
using Mersal.Auth;

namespace Mersal.Authz;

/// <summary>
/// The field-level primitive: strips field-classes the caller may not read, in code — minimum-necessary
/// is not a comment (11-permission-matrix.md; 18-security-model.md §4). Reception must not receive
/// diagnosis; labs not prescriptions; pharmacies not investigation results; finance not diagnoses.
/// A strip is audited so a min-necessary denial is observable.
/// </summary>
public sealed class FieldProjector(FieldAccessMatrix matrix, IAuditClient audit)
{
    /// <summary>
    /// Project a record (field-name → (value, field-class)) down to the classes the role may read,
    /// dropping the rest. Returns the allowed subset and audits any stripped classes.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>> ProjectAsync(
        HbmpPrincipal principal,
        string resourceType,
        IReadOnlyDictionary<string, (object? Value, string FieldClass)> record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var allowedClasses = matrix.ReadableClasses(principal.Roles);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var stripped = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (name, (value, fieldClass)) in record)
        {
            if (allowedClasses.Contains(fieldClass)) result[name] = value;
            else stripped.Add(fieldClass);
        }

        if (stripped.Count > 0)
        {
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = resourceType,
                EntityId = "(projection)",
                Action = AuditAction.Read,
                ActorUserId = principal.Subject,
                ActorRole = string.Join(',', principal.Roles),
                DecisionOutcome = "field-strip",
                DecisionReasonCode = "min-necessary",
                FieldClasses = stripped.ToArray(),
            }, ct);
        }

        return result;
    }
}

/// <summary>
/// Which field-classes each role may read. The source of truth is 11-permission-matrix.md; this is the
/// in-code enforcement of it. Roles not listed get only the "public"/"operational" classes.
/// </summary>
public sealed class FieldAccessMatrix
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _roleToClasses;
    private static readonly IReadOnlySet<string> Baseline =
        new HashSet<string>(StringComparer.Ordinal) { "public", "operational", "identity" };

    public FieldAccessMatrix(IReadOnlyDictionary<string, IReadOnlySet<string>> roleToClasses)
        => _roleToClasses = roleToClasses;

    /// <summary>Union of readable field-classes across the principal's roles (+ baseline).</summary>
    public IReadOnlySet<string> ReadableClasses(IReadOnlySet<string> roles)
    {
        var set = new HashSet<string>(Baseline, StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (_roleToClasses.TryGetValue(role, out var classes)) set.UnionWith(classes);
        }
        return set;
    }
}
