using System.Collections.Concurrent;

namespace Mersal.Events;

/// <summary>
/// Records which event ids a consumer has already processed, so redelivery (at-least-once) is a no-op.
/// Consumers dedupe on event id (16-service-architecture.md). Backed per-service by a processed-events
/// table in production; the in-memory implementation serves tests/dev.
/// </summary>
public interface IProcessedEventStore
{
    Task<bool> TryBeginAsync(Guid eventId, CancellationToken ct = default);
}

/// <summary>Helper that wraps a handler with dedupe: runs it only the first time an event id is seen.</summary>
public sealed class IdempotentConsumer(IProcessedEventStore store)
{
    /// <summary>Returns true if the handler ran, false if the event was a duplicate and skipped.</summary>
    public async Task<bool> HandleAsync(Guid eventId, Func<CancellationToken, Task> handler, CancellationToken ct = default)
    {
        if (!await store.TryBeginAsync(eventId, ct)) return false; // already processed
        await handler(ct);
        return true;
    }
}

public sealed class InMemoryProcessedEventStore : IProcessedEventStore
{
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();

    public Task<bool> TryBeginAsync(Guid eventId, CancellationToken ct = default) =>
        Task.FromResult(_seen.TryAdd(eventId, 0));
}
