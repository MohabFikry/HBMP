using Mersal.Audit.Client;

namespace Mersal.Audit.Infrastructure;

/// <summary>EF-mapped row for <c>audit.audit_event</c>. Maps to/from the domain <see cref="AuditEvent"/>.</summary>
public sealed class AuditEventRow
{
    public Guid AuditEventId { get; set; }
    public string PartitionKey { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string SourceService { get; set; } = default!;
    public string EntityType { get; set; } = default!;
    public string EntityId { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string Severity { get; set; } = default!;

    public string? ActorUserId { get; set; }
    public string? ActorRole { get; set; }
    public string? TenantId { get; set; }
    public string? ProviderId { get; set; }
    public string? SessionId { get; set; }
    public bool ActorMfa { get; set; }

    public string? BeforeState { get; set; }
    public string? AfterState { get; set; }
    public string[] FieldClasses { get; set; } = [];

    public string? DecisionOutcome { get; set; }
    public string? DecisionPolicyId { get; set; }
    public string? DecisionReasonCode { get; set; }

    public string? Purpose { get; set; }
    public bool BreakGlass { get; set; }

    public string? CorrelationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long Seq { get; set; }

    public string? PrevHash { get; set; }
    public string RecordHash { get; set; } = default!;

    public static AuditEventRow FromDomain(AuditEvent e, string partitionKey) => new()
    {
        AuditEventId = e.AuditEventId,
        PartitionKey = partitionKey,
        ServiceName = e.ServiceName,
        SourceService = e.SourceService,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        Action = e.Action.ToString(),
        Severity = e.Severity.ToString(),
        ActorUserId = e.ActorUserId,
        ActorRole = e.ActorRole,
        TenantId = e.TenantId,
        ProviderId = e.ProviderId,
        SessionId = e.SessionId,
        ActorMfa = e.ActorMfa,
        BeforeState = e.BeforeState,
        AfterState = e.AfterState,
        FieldClasses = e.FieldClasses.ToArray(),
        DecisionOutcome = e.DecisionOutcome,
        DecisionPolicyId = e.DecisionPolicyId,
        DecisionReasonCode = e.DecisionReasonCode,
        Purpose = e.Purpose,
        BreakGlass = e.BreakGlass,
        CorrelationId = e.CorrelationId,
        OccurredAt = e.OccurredAt,
        PrevHash = e.PrevHash,
        RecordHash = e.RecordHash!,
    };

    public AuditEvent ToDomain() => new()
    {
        AuditEventId = AuditEventId,
        ServiceName = ServiceName,
        SourceService = SourceService,
        EntityType = EntityType,
        EntityId = EntityId,
        Action = Enum.Parse<AuditAction>(Action),
        Severity = Enum.Parse<AuditSeverity>(Severity),
        ActorUserId = ActorUserId,
        ActorRole = ActorRole,
        TenantId = TenantId,
        ProviderId = ProviderId,
        SessionId = SessionId,
        ActorMfa = ActorMfa,
        BeforeState = BeforeState,
        AfterState = AfterState,
        FieldClasses = FieldClasses,
        DecisionOutcome = DecisionOutcome,
        DecisionPolicyId = DecisionPolicyId,
        DecisionReasonCode = DecisionReasonCode,
        Purpose = Purpose,
        BreakGlass = BreakGlass,
        CorrelationId = CorrelationId,
        OccurredAt = OccurredAt,
        PrevHash = PrevHash,
        RecordHash = RecordHash,
    };
}
