using Mersal.Audit.Client;
using Mersal.Migration.Core;

namespace Mersal.Migration.Streams;

/// <summary>A provider user as landed by the migration — used to verify isolation post-load.</summary>
public sealed record ProviderUserRow(string SourceId, string ProviderId, string UserId, string Username, string Role);

/// <summary>
/// STREAM B — providers (phase 12.1). Imports provider organizations/locations/contracts/users and
/// lands provider users with provenance. Natural key = source_system:provider_id:user_id, so
/// re-runs upsert in place. After load, <see cref="ProviderIsolationVerifier"/> proves each user is
/// scoped only to its own provider (../11 isolation) before users are enabled.
/// </summary>
public sealed class ProviderStream(IMigrationSink sink, IAuditClient audit, TimeProvider clock)
{
    public const string StreamName = "providers";

    public async Task<(ReconciliationReport Reconciliation, IReadOnlyList<ProviderUserRow> Users)> RunAsync(
        MigrationBatch batch, StreamConfig config, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rows);

        var recon = new ReconciliationReport(StreamName, batch.BatchId) { SourceCount = rows.Count };
        var users = new List<ProviderUserRow>();
        var now = clock.GetUtcNow();

        foreach (var row in rows)
        {
            var sourceId = StreamSupport.SourceId(row);
            var missing = StreamSupport.MissingRequired(config, row);
            if (missing.Count > 0) { recon.Reject(sourceId, $"missing required: {string.Join(",", missing)}"); continue; }

            var providerId = row.GetValueOrDefault("provider_id");
            var userId = row.GetValueOrDefault("user_id");
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(userId))
            {
                recon.Reject(sourceId, "missing provider_id/user_id");
                continue;
            }

            var naturalKey = $"{batch.SourceSystem}:{providerId}:{userId}";
            var payload = StreamSupport.BuildPayload(config, row, recon);
            var provenance = StreamSupport.Provenance(batch, sourceId, now);
            var result = await sink.UpsertAsync(StreamName, naturalKey, payload, provenance, ct);
            if (result == UpsertResult.Inserted) recon.Inserted++; else recon.Updated++;

            await StreamSupport.AuditAsync(audit, batch, "provider_user", naturalKey, result, provenance, ct);

            users.Add(new ProviderUserRow(sourceId, providerId!, userId!,
                row.GetValueOrDefault("username") ?? userId!, row.GetValueOrDefault("role") ?? "provider_user"));
        }

        return (recon, users);
    }
}
