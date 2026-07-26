using System.Collections.Concurrent;
using Mersal.Audit.Client;

namespace Mersal.Migration.Tests;

/// <summary>Captures the migration audit trail so tests can assert every load emitted an event.</summary>
public sealed class CapturingAudit : IAuditClient
{
    public ConcurrentQueue<AuditEventDraft> Events { get; } = new();

    public ValueTask EmitAsync(AuditEventDraft draft, CancellationToken ct = default)
    {
        Events.Enqueue(draft);
        return ValueTask.CompletedTask;
    }
}
