using Mersal.Audit.Client;

namespace Mersal.Audit.Domain;

/// <summary>
/// Ingests an audit event delivered from RabbitMQ: dedupes on event id (at-least-once delivery),
/// chains it onto the tail of its monthly partition, appends it append-only, and persists a WORM
/// copy. This is the single write path — there is no synchronous write from business services.
/// See 19-audit-strategy.md §4 and phase-0-foundations.md (0.3).
/// </summary>
public sealed class AuditIngestService(IAuditEventStore store, IWormStore worm)
{
    public async Task<IngestResult> IngestAsync(AuditEvent raw, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        // Idempotent: a duplicate delivery of the same event id is a no-op.
        if (await store.ExistsAsync(raw.AuditEventId, ct))
        {
            return IngestResult.Duplicate;
        }

        var partition = AuditPartition.KeyFor(raw.OccurredAt);
        var prevHash = await store.GetLastRecordHashAsync(partition, ct);
        var chained = HashChain.Chain(raw, prevHash);

        // RDBMS append-only first (source of truth for ordering), then the WORM mirror.
        await store.AppendAsync(chained, ct);
        await worm.PersistAsync(chained, ct);

        return IngestResult.Appended;
    }
}

public enum IngestResult
{
    Appended,
    Duplicate,
}
