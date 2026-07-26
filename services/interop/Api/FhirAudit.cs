using Mersal.Audit.Client;
using Mersal.Auth;

namespace Mersal.Interop.Api;

/// <summary>
/// Emits the hash-chained audit event that every FHIR interaction requires (13.1 acceptance; 19-audit-strategy).
/// This is IN ADDITION to the authorization audit the engine already writes on deny/sensitive-allow: here we
/// record the successful PHI read / search / create at the FHIR boundary, with the resource, ids, and the field
/// CLASSES touched (never raw PHI values). Exports/bulk are high-severity.
/// </summary>
public sealed class FhirAudit(IAuditClient audit)
{
    public ValueTask ReadAsync(HbmpPrincipal p, string resourceType, string id, bool sensitive, CancellationToken ct) =>
        audit.EmitAsync(Draft(p, resourceType, id, AuditAction.Read, sensitive, matched: 1), ct);

    public ValueTask SearchAsync(HbmpPrincipal p, string resourceType, int matched, bool sensitive, CancellationToken ct) =>
        audit.EmitAsync(Draft(p, resourceType, "(search)", AuditAction.Read, sensitive, matched), ct);

    public ValueTask CreateAsync(HbmpPrincipal p, string resourceType, string? id, CancellationToken ct) =>
        audit.EmitAsync(Draft(p, resourceType, id ?? "(created)", AuditAction.Create, sensitive: true, matched: 1), ct);

    private static AuditEventDraft Draft(HbmpPrincipal p, string resourceType, string id, AuditAction action, bool sensitive, int matched) =>
        new()
        {
            EntityType = $"fhir:{resourceType}",
            EntityId = id,
            Action = action,
            ActorUserId = p.Subject,
            ActorRole = string.Join(',', p.Roles),
            TenantId = p.TenantId,
            ProviderId = p.ProviderId,
            SessionId = p.SessionId,
            ActorMfa = p.MfaSatisfied,
            Purpose = "fhir-facade",
            FieldClasses = FieldClassesFor(resourceType),
            AfterState = action == AuditAction.Read ? $"matched={matched}" : null,
            Severity = sensitive ? AuditSeverity.Notice : AuditSeverity.Info,
        };

    /// <summary>The field CLASS the FHIR resource exposes (drives min-necessary audit analytics; never values).</summary>
    private static string[] FieldClassesFor(string resourceType) => resourceType switch
    {
        "Condition" => ["diagnosis"],
        "DiagnosticReport" or "Observation" => ["clinical-result"],
        "MedicationRequest" => ["prescription"],
        "AllergyIntolerance" => ["clinical"],
        "Coverage" => ["financials", "coverage"],
        "Patient" => ["pii"],
        "Encounter" => ["clinical"],
        "ServiceRequest" => ["order"],
        _ => ["pii"],
    };
}
