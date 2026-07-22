using Mersal.Audit.Client;

namespace Mersal.Events;

/// <summary>
/// Routes <c>libs/audit-client</c> emits through the transactional outbox — the durable
/// <see cref="IAuditOutbox"/> that replaces the in-memory placeholder from 0.3. Audit events are
/// staged in the same transaction as the business change and relayed to the audit-service queue
/// (default <c>audit.events</c>), so an audit emit can never be lost.
/// </summary>
public sealed class OutboxAuditSink(IOutbox outbox) : IAuditOutbox
{
    public const string Destination = "audit.events";

    public ValueTask EnqueueAsync(AuditEvent auditEvent, CancellationToken ct = default) =>
        outbox.EnqueueAsync("AuditEventRecorded", Destination, auditEvent, ct);
}
