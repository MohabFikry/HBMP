namespace Mersal.Events;

/// <summary>
/// A domain event staged in the transactional outbox: written in the SAME DB transaction as the state
/// change, then relayed to RabbitMQ / NATS by the dispatcher. Event naming is
/// <c>&lt;Domain&gt;&lt;PastTenseVerb&gt;</c> (e.g. OrderLineConsumed). See 16-service-architecture.md.
/// </summary>
public sealed class OutboxMessage
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = default!;
    public string Destination { get; set; } = default!;   // queue / subject
    public string Payload { get; set; } = default!;        // JSON (CloudEvents-compatible)
    public string? CorrelationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
