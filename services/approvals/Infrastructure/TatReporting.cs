using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Infrastructure;

/// <summary>Turnaround-time / SLA aggregate over decided authorizations (phase 7.3), for the reporting read-model
/// (phase 8). Count + avg + p95 TAT + SLA-breach count, overall and per status. p95 uses Postgres
/// <c>percentile_cont</c> (not expressible in LINQ), so this is a hand-authored grouped query.</summary>
public sealed record TatBucket(string Status, long Count, double AvgTatSeconds, double P95TatSeconds, long SlaBreaches);
public sealed record TatSummary(long Total, double AvgTatSeconds, double P95TatSeconds, long SlaBreaches, IReadOnlyList<TatBucket> ByStatus);

public static class TatReporting
{
    public static async Task<TatSummary> SummaryAsync(ApprovalsDbContext db, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            var buckets = new List<TatBucket>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT status,
                           count(*)                                                          AS n,
                           coalesce(avg(tat_seconds), 0)                                      AS avg_tat,
                           coalesce(percentile_cont(0.95) WITHIN GROUP (ORDER BY tat_seconds), 0) AS p95_tat,
                           count(*) FILTER (WHERE sla_breached)                               AS breaches
                    FROM approvals.authorization
                    WHERE tat_seconds IS NOT NULL
                    GROUP BY status;";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    buckets.Add(new TatBucket(
                        r.GetString(0), r.GetInt64(1),
                        Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture),
                        Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture),
                        r.GetInt64(4)));
            }

            long total = 0, breaches = 0; double avg = 0, p95 = 0;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT count(*)                                                          AS n,
                           coalesce(avg(tat_seconds), 0)                                      AS avg_tat,
                           coalesce(percentile_cont(0.95) WITHIN GROUP (ORDER BY tat_seconds), 0) AS p95_tat,
                           count(*) FILTER (WHERE sla_breached)                               AS breaches
                    FROM approvals.authorization
                    WHERE tat_seconds IS NOT NULL;";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    total = r.GetInt64(0);
                    avg = Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture);
                    p95 = Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture);
                    breaches = r.GetInt64(3);
                }
            }
            return new TatSummary(total, avg, p95, breaches, buckets);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}
