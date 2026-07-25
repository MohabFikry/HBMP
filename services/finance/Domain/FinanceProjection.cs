using System.Reflection;

namespace Mersal.Finance.Domain;

/// <summary>
/// The field-level projection control (phase 10.2) — the structural guarantee that <b>Finance can never surface a
/// clinical/diagnosis field</b> (11-permission-matrix §4 "Finance <c>diagnosis</c> = ❌"). Every finance DTO
/// implements <see cref="IFinanceProjection"/>; the whitelist below names the ONLY field classes finance may
/// expose (billing service code, quantities, amounts, masked-min PII, coverage category, provider, period). The
/// <see cref="Guard"/> reflects over a type graph and REJECTS any property whose name matches a clinical token —
/// so a clinical field cannot be added to a finance DTO without failing the build/unit guard.
/// </summary>
public interface IFinanceProjection;

public static class FinanceProjection
{
    /// <summary>The only field classes a finance projection may expose (10 §3.12; 18-security §8).</summary>
    public static readonly IReadOnlySet<string> AllowedClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "service_code", "quantity", "amount", "provider", "coverage_category", "period", "pii_masked",
    };

    /// <summary>Clinical tokens that must NEVER appear in a finance DTO property name. Substring match, lower-cased.</summary>
    public static readonly IReadOnlyList<string> ForbiddenTokens =
    [
        "diagnosis", "icd", "clinical", "emrnote", "note", "symptom", "allergy",
        "prescription", "rxdetail", "medicationname", "labresult", "imagingresult", "result",
    ];

    /// <summary>Property names that contain a forbidden token but are legitimate finance fields → not clinical.</summary>
    private static readonly IReadOnlySet<string> Exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // none currently — the DTOs are named to avoid collisions (e.g. "ServiceCode", not "resultCode").
    };

    /// <summary>Returns the offending property names (empty = clean). Recurses one level into nested projection
    /// types so a masked sub-DTO is checked too.</summary>
    public static IReadOnlyList<string> Offenders(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var bad = new List<string>();
        Inspect(type, bad, depth: 0);
        return bad;
    }

    /// <summary>Throws if <paramref name="type"/> exposes any clinical field. Called by the unit guard for every
    /// finance DTO — the compile-time-adjacent proof of the invariant.</summary>
    public static void Guard(Type type)
    {
        var offenders = Offenders(type);
        if (offenders.Count > 0)
            throw new InvalidOperationException(
                $"Finance projection {type.Name} exposes forbidden clinical field(s): {string.Join(", ", offenders)}. " +
                "Finance ≠ diagnosis (11-permission-matrix §4).");
    }

    private static void Inspect(Type type, List<string> bad, int depth)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var lower = p.Name.ToLowerInvariant();
            if (!Exempt.Contains(p.Name) && ForbiddenTokens.Any(lower.Contains))
                bad.Add($"{type.Name}.{p.Name}");

            if (depth >= 2) continue;
            var t = Unwrap(p.PropertyType);
            if (typeof(IFinanceProjection).IsAssignableFrom(t))
                Inspect(t, bad, depth + 1);
        }
    }

    private static Type Unwrap(Type t)
    {
        if (t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
            return t.GetGenericArguments()[0];
        return Nullable.GetUnderlyingType(t) ?? t;
    }
}
