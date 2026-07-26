using Mersal.Audit.Client;
using Mersal.Migration.Core;

namespace Mersal.Migration.Streams;

/// <summary>
/// STREAM C — beneficiaries (phase 12.1). Per source row: normalize the identifier, dedupe against
/// the known set (existing store + everyone merged earlier in this batch), then route —
/// <list type="bullet">
///   <item>invalid identifier → rejected (triage list);</item>
///   <item>dedupe review → HELD in the review queue, never auto-merged;</item>
///   <item>auto-merge → update the matched person in place (idempotent);</item>
///   <item>no match → insert a new beneficiary.</item>
/// </list>
/// Emits a reconciliation report (must balance) and a dedupe report; every load is provenance-tagged
/// and hash-chain audited.
/// </summary>
public sealed class BeneficiaryStream(IMigrationSink sink, IAuditClient audit, TimeProvider clock)
{
    public const string StreamName = "beneficiaries";

    public async Task<(ReconciliationReport Reconciliation, DedupeReport Dedupe)> RunAsync(
        MigrationBatch batch, StreamConfig config, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        IReadOnlyList<KnownPerson> existing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rows);

        var recon = new ReconciliationReport(StreamName, batch.BatchId) { SourceCount = rows.Count };
        var dedupe = new DedupeReport();
        var known = new List<KnownPerson>(existing ?? []);
        var now = clock.GetUtcNow();

        foreach (var row in rows)
        {
            var sourceId = StreamSupport.SourceId(row);

            var missing = StreamSupport.MissingRequired(config, row);
            if (missing.Count > 0)
            {
                recon.Reject(sourceId, $"missing required: {string.Join(",", missing)}");
                continue;
            }

            var idRaw = row.GetValueOrDefault("national_id") ?? row.GetValueOrDefault("unhcr_id")
                        ?? row.GetValueOrDefault("passport") ?? row.GetValueOrDefault("identifier");
            var kindHint = row.ContainsKey("national_id") ? IdentifierKind.NationalId
                : row.ContainsKey("unhcr_id") ? IdentifierKind.Unhcr
                : row.ContainsKey("passport") ? IdentifierKind.Passport : IdentifierKind.Unknown;
            var identifier = IdentifierNormalizer.Normalize(idRaw, kindHint);
            if (!identifier.IsValid)
            {
                recon.Reject(sourceId, $"invalid identifier: {identifier.Reason}");
                continue;
            }

            var candidate = new DedupeCandidate(sourceId, identifier,
                row.GetValueOrDefault("full_name") ?? string.Empty, ParseDate(row.GetValueOrDefault("birth_date")));
            var outcome = Dedupe.Match(candidate, known);
            dedupe.Add(outcome);

            if (outcome.Decision == MatchDecision.Review)
            {
                recon.Held++; // parked for human sign-off before promotion — deliberately NOT loaded.
                continue;
            }

            var naturalKey = outcome.Decision == MatchDecision.AutoMerge && outcome.MatchedId is not null
                ? outcome.MatchedId
                : identifier.Key;

            var payload = StreamSupport.BuildPayload(config, row, recon);
            var provenance = StreamSupport.Provenance(batch, sourceId, now);
            var result = await sink.UpsertAsync(StreamName, naturalKey, payload, provenance, ct);
            if (result == UpsertResult.Inserted) recon.Inserted++; else recon.Updated++;

            await StreamSupport.AuditAsync(audit, batch, "beneficiary", naturalKey, result, provenance, ct);

            // Newly inserted people become matchable for the rest of the batch (in-batch dedupe).
            if (result == UpsertResult.Inserted)
                known.Add(new KnownPerson(naturalKey, [identifier.Key], candidate.FullName, candidate.BirthDate));
        }

        return (recon, dedupe);
    }

    private static DateOnly? ParseDate(string? s)
        => DateOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}
