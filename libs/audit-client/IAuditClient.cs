using System.Diagnostics;

namespace Mersal.Audit.Client;

/// <summary>
/// The shared client every service uses to emit audit events. It stamps correlation_id from the
/// ambient W3C trace context, assigns a uuid, and writes the event to the emitting service's
/// transactional outbox (fire-and-forget durable — an emit cannot be lost, and never silently
/// no-ops in production). audit-service consumes the outbox, chains + WORM-persists the record.
/// See 19-audit-strategy.md §5 (correlation) and phase-0-foundations.md (0.3).
/// </summary>
public interface IAuditClient
{
    /// <summary>Emit an audit event durably (enqueued to the outbox in the caller's transaction).</summary>
    ValueTask EmitAsync(AuditEventDraft draft, CancellationToken ct = default);
}

/// <summary>
/// The caller-supplied portion of an audit event. Correlation, id, timing and source are filled in
/// by <see cref="IAuditClient"/>; the hash chain is filled by audit-service on ingest.
/// </summary>
public sealed record AuditEventDraft
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required AuditAction Action { get; init; }

    public string? ActorUserId { get; init; }
    public string? ActorRole { get; init; }
    public string? TenantId { get; init; }
    public string? ProviderId { get; init; }
    public string? SessionId { get; init; }
    public bool ActorMfa { get; init; }

    public string? BeforeState { get; init; }
    public string? AfterState { get; init; }
    public IReadOnlyList<string> FieldClasses { get; init; } = [];

    public string? DecisionOutcome { get; init; }
    public string? DecisionPolicyId { get; init; }
    public string? DecisionReasonCode { get; init; }

    public string? Purpose { get; init; }
    public bool BreakGlass { get; init; }
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;
}

/// <summary>
/// Default <see cref="IAuditClient"/>: assigns identity/correlation/timing and hands the event to
/// the outbox. Correlation id comes from <see cref="Activity.Current"/> (traceparent) automatically.
/// </summary>
public sealed class AuditClient(IAuditOutbox outbox, IAuditClientContext context, TimeProvider clock) : IAuditClient
{
    public async ValueTask EmitAsync(AuditEventDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var evt = new AuditEvent
        {
            AuditEventId = Guid.NewGuid(),
            ServiceName = context.ServiceName,
            SourceService = context.ServiceName,
            EntityType = draft.EntityType,
            EntityId = draft.EntityId,
            Action = draft.Action,
            ActorUserId = draft.ActorUserId,
            ActorRole = draft.ActorRole,
            TenantId = draft.TenantId,
            ProviderId = draft.ProviderId,
            SessionId = draft.SessionId,
            ActorMfa = draft.ActorMfa,
            BeforeState = draft.BeforeState,
            AfterState = draft.AfterState,
            FieldClasses = draft.FieldClasses,
            DecisionOutcome = draft.DecisionOutcome,
            DecisionPolicyId = draft.DecisionPolicyId,
            DecisionReasonCode = draft.DecisionReasonCode,
            Purpose = draft.Purpose,
            BreakGlass = draft.BreakGlass,
            Severity = draft.Severity,
            CorrelationId = Activity.Current?.TraceId.ToString() ?? context.FallbackCorrelationId,
            OccurredAt = clock.GetUtcNow(),
        };

        await outbox.EnqueueAsync(evt, ct);
    }
}

/// <summary>Ambient context for the audit client (the emitting service's identity).</summary>
public interface IAuditClientContext
{
    string ServiceName { get; }
    string? FallbackCorrelationId { get; }
}

public sealed record AuditClientContext(string ServiceName, string? FallbackCorrelationId = null) : IAuditClientContext;
