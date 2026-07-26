using System.Text.Json;
using Mersal.Audit.Client;
using Mersal.Migration.Core;

namespace Mersal.Migration.Streams;

/// <summary>Shared helpers for mapping/validating source rows and emitting the migration audit trail.</summary>
internal static class StreamSupport
{
    public const string SourceIdField = "source_id";

    public static string SourceId(IReadOnlyDictionary<string, string?> row)
        => row.TryGetValue(SourceIdField, out var v) && !string.IsNullOrWhiteSpace(v) ? v! : "(missing source_id)";

    /// <summary>Missing required target fields for this row (empty when all present).</summary>
    public static IReadOnlyList<string> MissingRequired(StreamConfig config, IReadOnlyDictionary<string, string?> row)
        => config.Required
            .Where(m => !row.TryGetValue(m.SourceField, out var v) || string.IsNullOrWhiteSpace(v))
            .Select(m => m.TargetField)
            .ToList();

    /// <summary>Map source → target fields per config, count coverage, and serialize the payload.</summary>
    public static string BuildPayload(StreamConfig config, IReadOnlyDictionary<string, string?> row, ReconciliationReport report)
    {
        var payload = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var m in config.Mappings)
        {
            if (row.TryGetValue(m.SourceField, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                payload[m.TargetField] = v;
                report.CountField(m.TargetField);
            }
        }
        return JsonSerializer.Serialize(payload);
    }

    public static Provenance Provenance(MigrationBatch batch, string sourceId, DateTimeOffset now) => new()
    {
        SourceSystem = batch.SourceSystem,
        SourceId = sourceId,
        BatchId = batch.BatchId,
        LoadedAt = now,
    };

    /// <summary>Emit the hash-chained migration audit event for a landed row (audit-service chains on ingest).</summary>
    public static ValueTask AuditAsync(
        IAuditClient audit, MigrationBatch batch, string entityType, string entityId,
        UpsertResult result, Provenance provenance, CancellationToken ct)
        => audit.EmitAsync(new AuditEventDraft
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = result == UpsertResult.Inserted ? AuditAction.Create : AuditAction.Update,
            ActorRole = "migration",
            Purpose = $"data-migration:{batch.Stream}",
            AfterState = JsonSerializer.Serialize(new
            {
                provenance.SourceSystem, provenance.SourceId, batchId = provenance.BatchId,
                batch.ConfigVersion, batch.Environment, batch.Masked,
            }),
            Severity = AuditSeverity.Info,
        }, ct);
}
