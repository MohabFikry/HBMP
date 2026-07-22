namespace Mersal.Audit.Client;

/// <summary>
/// A single immutable audit record (22-data-dictionary.md §10.4, 19-audit-strategy.md §3).
/// Emitted by <see cref="IAuditClient"/>, chained + persisted append-only by audit-service.
/// before/after snapshots are MINIMIZED — never raw PHI values; field-classes capture what changed.
/// </summary>
public sealed record AuditEvent
{
    /// <summary>uuid v7, assigned at emit; also the dedupe key for at-least-once delivery.</summary>
    public required Guid AuditEventId { get; init; }

    public required string ServiceName { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required AuditAction Action { get; init; }

    // -------- Actor context --------
    public string? ActorUserId { get; init; }
    public string? ActorRole { get; init; }
    public string? TenantId { get; init; }
    public string? ProviderId { get; init; }
    public string? SessionId { get; init; }
    /// <summary>acr/amr summary — was the actor MFA-backed.</summary>
    public bool ActorMfa { get; init; }

    // -------- Change snapshots (MINIMIZED, PHI-free) --------
    public string? BeforeState { get; init; }
    public string? AfterState { get; init; }
    /// <summary>Field classes touched (e.g. "diagnosis","financials","pii") — not raw values.</summary>
    public IReadOnlyList<string> FieldClasses { get; init; } = [];

    // -------- Decision context (for DECISION actions) --------
    public string? DecisionOutcome { get; init; }
    public string? DecisionPolicyId { get; init; }
    public string? DecisionReasonCode { get; init; }

    // -------- Purpose / break-glass --------
    public string? Purpose { get; init; }
    public bool BreakGlass { get; init; }

    // -------- Correlation + timing + severity --------
    public string? CorrelationId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;
    public required string SourceService { get; init; }

    // -------- Hash chain (assigned by audit-service on ingest) --------
    /// <summary>Hash of the previous record in this partition (null for the first).</summary>
    public string? PrevHash { get; init; }
    /// <summary>sha256 of the canonicalized record excluding record_hash itself.</summary>
    public string? RecordHash { get; init; }
}
