namespace Mersal.Migration.Core;

public enum UpsertResult { Inserted, Updated }

/// <summary>A landed (migrated) row, tagged with provenance and a soft-active flag.</summary>
public sealed record LandingRow(string Stream, string NaturalKey, string Payload, Provenance Provenance, bool Active);

/// <summary>
/// The load/revert boundary a stream writes through. Two invariants make a migration safe
/// (phase 12.1): UPSERT on (stream, natural key) is idempotent — re-running a batch updates in
/// place and never duplicates — and ROLLBACK-BY-BATCH soft-reverts exactly the rows a batch
/// touched, leaving pre-existing rows untouched. Implemented in-memory (tests) and over Postgres
/// (<see cref="Db.PostgresSink"/>, the real staging/prod sink).
/// </summary>
public interface IMigrationSink
{
    Task<UpsertResult> UpsertAsync(string stream, string naturalKey, string payloadJson, Provenance provenance, CancellationToken ct = default);
    Task<int> RollbackBatchAsync(Guid batchId, CancellationToken ct = default);
    Task<IReadOnlyList<LandingRow>> ActiveRowsAsync(string stream, CancellationToken ct = default);
}

/// <summary>Deterministic in-memory sink for unit tests and dry-run previews.</summary>
public sealed class InMemorySink : IMigrationSink
{
    private readonly Dictionary<(string Stream, string Key), LandingRow> _rows = new();

    public Task<UpsertResult> UpsertAsync(string stream, string naturalKey, string payloadJson, Provenance provenance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var existed = _rows.ContainsKey((stream, naturalKey));
        _rows[(stream, naturalKey)] = new LandingRow(stream, naturalKey, payloadJson, provenance, Active: true);
        return Task.FromResult(existed ? UpsertResult.Updated : UpsertResult.Inserted);
    }

    public Task<int> RollbackBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        var reverted = 0;
        foreach (var (key, row) in _rows.Where(r => r.Value.Active && r.Value.Provenance.BatchId == batchId).ToList())
        {
            _rows[key] = row with { Active = false };
            reverted++;
        }
        return Task.FromResult(reverted);
    }

    public Task<IReadOnlyList<LandingRow>> ActiveRowsAsync(string stream, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LandingRow>>(
            _rows.Values.Where(r => r.Active && r.Stream == stream).ToList());
}
