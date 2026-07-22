using System.Diagnostics;
using System.Text.Json;

namespace Mersal.Events;

/// <summary>
/// Stages domain events for publication. Implementations enqueue into the SAME persistence transaction
/// as the business state change (transactional outbox), so an event is never published without its
/// state change and vice-versa. Correlation id is stamped from the ambient W3C trace context.
/// </summary>
public interface IOutbox
{
    ValueTask EnqueueAsync<T>(string eventType, string destination, T payload, CancellationToken ct = default);
    ValueTask EnqueueRawAsync(OutboxMessage message, CancellationToken ct = default);
}

/// <summary>Reads pending messages and marks them processed — used by the relay/dispatcher.</summary>
public interface IOutboxReader
{
    Task<IReadOnlyList<OutboxMessage>> DequeueBatchAsync(int max, CancellationToken ct = default);
    Task MarkProcessedAsync(Guid eventId, CancellationToken ct = default);
    Task MarkFailedAsync(Guid eventId, string error, CancellationToken ct = default);
}

/// <summary>Publishes a relayed outbox message to the broker (RabbitMQ ordered / NATS fan-out).</summary>
public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct = default);
}

/// <summary>Base helper that stamps id/correlation/timing and serializes the payload.</summary>
public abstract class OutboxBase : IOutbox
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ValueTask EnqueueAsync<T>(string eventType, string destination, T payload, CancellationToken ct = default)
    {
        var msg = new OutboxMessage
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Destination = destination,
            Payload = JsonSerializer.Serialize(payload, Json),
            CorrelationId = Activity.Current?.TraceId.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        return EnqueueRawAsync(msg, ct);
    }

    public abstract ValueTask EnqueueRawAsync(OutboxMessage message, CancellationToken ct = default);
}
