using System.Collections.Concurrent;

namespace Mersal.Events;

/// <summary>
/// In-memory outbox + reader for tests and Tier 1 dev until a service wires the EF/DB-backed outbox.
/// Preserves the enqueue → dequeue → mark-processed contract so relay logic is exercised offline.
/// </summary>
public sealed class InMemoryOutbox : OutboxBase, IOutboxReader
{
    private readonly ConcurrentQueue<OutboxMessage> _pending = new();
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _all = new();

    public override ValueTask EnqueueRawAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _all[message.EventId] = message;
        _pending.Enqueue(message);
        return ValueTask.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> DequeueBatchAsync(int max, CancellationToken ct = default)
    {
        var batch = new List<OutboxMessage>();
        while (batch.Count < max && _pending.TryDequeue(out var m))
        {
            if (m.ProcessedAt is null) batch.Add(m);
        }
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(batch);
    }

    public Task MarkProcessedAsync(Guid eventId, CancellationToken ct = default)
    {
        if (_all.TryGetValue(eventId, out var m)) m.ProcessedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid eventId, string error, CancellationToken ct = default)
    {
        if (_all.TryGetValue(eventId, out var m)) { m.Attempts++; m.LastError = error; _pending.Enqueue(m); }
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<OutboxMessage> AllMessages => _all.Values.ToArray();

    /// <summary>Reset between tests.</summary>
    public void Clear()
    {
        _all.Clear();
        while (_pending.TryDequeue(out _)) { }
    }
}
