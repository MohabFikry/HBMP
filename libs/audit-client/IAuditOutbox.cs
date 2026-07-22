namespace Mersal.Audit.Client;

/// <summary>
/// The transactional outbox sink for audit events. In a real service this writes the event to the
/// service's <c>outbox</c> table inside the current business transaction (via libs/events, 0.5),
/// so the audit emit commits atomically with the state change and is relayed to RabbitMQ.
///
/// It must be durable and must NOT silently no-op in production (CLAUDE.md § Audit). A test/dev
/// implementation may collect in memory, but there is no production build that compiles it out.
/// </summary>
public interface IAuditOutbox
{
    ValueTask EnqueueAsync(AuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>
/// In-memory outbox for unit tests and until libs/events wires the DB-backed outbox (0.5).
/// Captures events so tests can assert emission. NOT for production persistence.
/// </summary>
public sealed class InMemoryAuditOutbox : IAuditOutbox
{
    private readonly List<AuditEvent> _events = [];
    private readonly object _gate = new();

    public IReadOnlyList<AuditEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public ValueTask EnqueueAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        lock (_gate) _events.Add(auditEvent);
        return ValueTask.CompletedTask;
    }
}
