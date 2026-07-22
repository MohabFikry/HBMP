using Mersal.Audit.Client;

namespace Mersal.Audit.Domain;

/// <summary>
/// Append-only persistence for audit records. The single writer per partition, so hash chaining
/// is consistent. Implementations grant INSERT only — no UPDATE/DELETE within retention.
/// </summary>
public interface IAuditEventStore
{
    /// <summary>The record_hash of the last record in a partition, or null if the partition is empty.</summary>
    Task<string?> GetLastRecordHashAsync(string partitionKey, CancellationToken ct = default);

    /// <summary>Whether an event with this id was already ingested (at-least-once delivery dedupe).</summary>
    Task<bool> ExistsAsync(Guid auditEventId, CancellationToken ct = default);

    /// <summary>Append a fully-chained record (prev_hash + record_hash already set).</summary>
    Task AppendAsync(AuditEvent chained, CancellationToken ct = default);

    /// <summary>Read a partition's records in chain order (ascending) — for verification/reads.</summary>
    Task<IReadOnlyList<AuditEvent>> ReadPartitionAsync(string partitionKey, CancellationToken ct = default);
}

/// <summary>
/// Tamper-evident WORM copy of each record to MinIO (object-lock / legal-hold), the second,
/// independent store beyond the RDBMS (19-audit-strategy.md §4).
/// </summary>
public interface IWormStore
{
    Task PersistAsync(AuditEvent chained, CancellationToken ct = default);
}

/// <summary>Raises a critical alert when the integrity verifier finds a broken chain.</summary>
public interface IIntegrityAlerter
{
    Task RaiseAsync(string partitionKey, ChainVerification result, CancellationToken ct = default);
}
