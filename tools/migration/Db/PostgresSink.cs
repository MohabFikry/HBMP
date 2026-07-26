using System.Reflection;
using Dapper;
using Mersal.Migration.Core;
using Npgsql;

namespace Mersal.Migration.Db;

/// <summary>
/// The real staging/prod sink over the <c>migration</c> schema. UPSERT is a single
/// <c>INSERT … ON CONFLICT (stream, natural_key) DO UPDATE</c> (idempotent), and
/// <c>RETURNING (xmax = 0)</c> tells insert from update. Rollback-by-batch soft-reverts
/// (active=false) exactly the rows a batch touched. All dry-runs use masked data in staging.
/// </summary>
public sealed class PostgresSink(string connectionString) : IMigrationSink
{
    /// <summary>Apply the migration-schema DDL (idempotent) — the toolkit self-provisions its schema.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(ReadEmbeddedDdl(), cancellationToken: ct));
    }

    /// <summary>Record a batch header (call once at the start of a run).</summary>
    public async Task RegisterBatchAsync(MigrationBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO migration.batch (batch_id, stream, config_version, environment, source_system, started_at, masked)
            VALUES (@BatchId, @Stream, @ConfigVersion, @Environment, @SourceSystem, @StartedAt, @Masked)
            ON CONFLICT (batch_id) DO NOTHING
            """, batch, cancellationToken: ct));
    }

    public async Task<UpsertResult> UpsertAsync(
        string stream, string naturalKey, string payloadJson, Provenance provenance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var inserted = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            INSERT INTO migration.landing (stream, natural_key, payload, source_system, source_id, batch_id, loaded_at, active)
            VALUES (@stream, @naturalKey, @payload::jsonb, @sourceSystem, @sourceId, @batchId, @loadedAt, true)
            ON CONFLICT (stream, natural_key) DO UPDATE
              SET payload = EXCLUDED.payload, source_system = EXCLUDED.source_system,
                  source_id = EXCLUDED.source_id, batch_id = EXCLUDED.batch_id,
                  loaded_at = EXCLUDED.loaded_at, active = true
            RETURNING (xmax = 0)
            """,
            new
            {
                stream, naturalKey, payload = payloadJson,
                sourceSystem = provenance.SourceSystem, sourceId = provenance.SourceId,
                batchId = provenance.BatchId, loadedAt = provenance.LoadedAt,
            }, cancellationToken: ct));
        return inserted ? UpsertResult.Inserted : UpsertResult.Updated;
    }

    public async Task<int> RollbackBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE migration.landing SET active = false WHERE batch_id = @batchId AND active",
            new { batchId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<LandingRow>> ActiveRowsAsync(string stream, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string Stream, string NaturalKey, string Payload, string SourceSystem, string SourceId, Guid BatchId, DateTimeOffset LoadedAt)>(
            new CommandDefinition(
                """
                SELECT stream, natural_key AS NaturalKey, payload::text AS Payload,
                       source_system AS SourceSystem, source_id AS SourceId, batch_id AS BatchId, loaded_at AS LoadedAt
                FROM migration.landing WHERE stream = @stream AND active
                """, new { stream }, cancellationToken: ct));
        return rows.Select(r => new LandingRow(r.Stream, r.NaturalKey, r.Payload,
            new Provenance { SourceSystem = r.SourceSystem, SourceId = r.SourceId, BatchId = r.BatchId, LoadedAt = r.LoadedAt },
            Active: true)).ToList();
    }

    private static string ReadEmbeddedDdl()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var path = Path.Combine(dir, "Migrations", "0001_migration.sql");
        return File.ReadAllText(path);
    }
}
