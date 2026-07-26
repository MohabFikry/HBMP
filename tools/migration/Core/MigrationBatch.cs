namespace Mersal.Migration.Core;

/// <summary>
/// A single migration run. Every inserted/updated row is tagged with the batch id and its
/// provenance so a run can be reconciled and reversed as one unit (phase 12.1 / ../35 §5).
/// Batches are the reversibility boundary: rollback-by-batch reverts exactly the rows this run
/// touched and nothing pre-existing.
/// </summary>
public sealed record MigrationBatch
{
    public required Guid BatchId { get; init; }
    public required string Stream { get; init; }        // "master-data" | "providers" | "beneficiaries"
    public required string ConfigVersion { get; init; }  // versioned per-stream config (StreamConfig.Version)
    public required string Environment { get; init; }    // "staging" | "production" — prod is gated
    public required string SourceSystem { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required bool Masked { get; init; }           // lower envs MUST be masked (../25 §1)

    public static MigrationBatch Start(StreamConfig config, string environment, DateTimeOffset now, bool masked) => new()
    {
        BatchId = Guid.NewGuid(),
        Stream = config.Stream,
        ConfigVersion = config.Version,
        Environment = environment,
        SourceSystem = config.SourceSystem,
        StartedAt = now,
        Masked = masked,
    };
}

/// <summary>
/// Provenance carried on every migrated row: which source system + source id produced it, under
/// which batch. This is what makes a load auditable and idempotent (natural-key + source-id upsert)
/// and reversible (soft-revert by batch id).
/// </summary>
public sealed record Provenance
{
    public required string SourceSystem { get; init; }
    public required string SourceId { get; init; }
    public required Guid BatchId { get; init; }
    public required DateTimeOffset LoadedAt { get; init; }
}
