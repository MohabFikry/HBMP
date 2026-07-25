using Mersal.Reporting.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Reporting.Infrastructure;

/// <summary>Aggregate KPI queries over the read-model (phase 8.2). Every result is de-identified: coded values,
/// counts, amounts, timings — no beneficiary identifiers, no clinical free text. Operational reports are simple
/// grouped scans (NFR-006 p95 ≤ 3 s); p95 TAT is computed from the fact sample. Tenant-scoped throughout.</summary>
public sealed class ReportQueries(ReportingDbContext db, TimeProvider clock)
{
    public async Task<ApprovalTatReport> ApprovalTatAsync(string tenant, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var facts = await db.AuthorizationFacts.AsNoTracking()
            .Where(f => f.TenantId == tenant && f.Period >= from && f.Period <= to && f.TatSeconds != null)
            .Select(f => new { f.Priority, Tat = f.TatSeconds!.Value, f.SlaBreached })
            .ToListAsync(ct);

        TatRow Row(string dim, IReadOnlyList<(long Tat, bool Breach)> xs) =>
            new(dim, xs.Count, xs.Count == 0 ? 0 : xs.Average(x => x.Tat),
                Percentile.P95(xs.Select(x => x.Tat).ToList()), xs.Count(x => x.Breach));

        var byPriority = facts.GroupBy(f => f.Priority)
            .Select(g => Row(g.Key, g.Select(x => (x.Tat, x.SlaBreached)).ToList()))
            .OrderBy(r => r.Dimension).ToList();
        var all = facts.Select(x => (x.Tat, x.SlaBreached)).ToList();
        var overall = Row("all", all);
        return new ApprovalTatReport(overall.Count, overall.AvgTatSeconds, overall.P95TatSeconds, overall.SlaBreaches, byPriority);
    }

    public async Task<PendingApprovalsReport> PendingApprovalsAsync(string tenant, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var pending = await db.PendingAuthorizations.AsNoTracking().Where(p => p.TenantId == tenant).ToListAsync(ct);
        var rows = pending
            .GroupBy(p => (p.Status, p.Priority, Bucket: AgeBuckets.Of(now - p.SubmittedAt)))
            .Select(g => new PendingRow(g.Key.Status, g.Key.Priority, g.Key.Bucket, g.Count(), g.Count(x => x.SlaBreached || (x.SlaDueAt is { } d && d < now))))
            .OrderBy(r => r.Status).ThenBy(r => r.Priority).ToList();
        return new PendingApprovalsReport(pending.Count, rows.Sum(r => r.SlaBreaches), rows);
    }

    public async Task<ClinicWorkloadReport> ClinicWorkloadAsync(string tenant, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.EncounterFacts.AsNoTracking()
            .Where(f => f.TenantId == tenant && f.Kind == "Encounter" && f.Period >= from && f.Period <= to)
            .GroupBy(f => new { f.ClinicId, f.Period })
            .Select(g => new { g.Key.ClinicId, g.Key.Period, Sum = g.Sum(x => x.Count) })
            .OrderBy(r => r.ClinicId).ThenBy(r => r.Period).ToListAsync(ct);
        return new ClinicWorkloadReport(rows.Select(r => new WorkloadRow(r.ClinicId, r.Period, r.Sum)).ToList());
    }

    public async Task<UtilizationReport> UtilizationAsync(string tenant, UtilizationDimension dimension, DateOnly from, DateOnly to, int top = 25, CancellationToken ct = default)
    {
        var dim = dimension.ToString();
        var rows = await db.UtilizationFacts.AsNoTracking()
            .Where(f => f.TenantId == tenant && f.Dimension == dim && f.Period >= from && f.Period <= to)
            .GroupBy(f => f.Code)
            .Select(g => new { Code = g.Key, Sum = g.Sum(x => x.Count) })
            .OrderByDescending(r => r.Sum).Take(top).ToListAsync(ct);
        return new UtilizationReport(dim, rows.Select(r => new UtilizationRow(r.Code, r.Sum)).ToList());
    }

    public async Task<NoShowReport> NoShowAsync(string tenant, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var facts = await db.EncounterFacts.AsNoTracking()
            .Where(f => f.TenantId == tenant && f.Period >= from && f.Period <= to
                        && (f.Kind == "Booked" || f.Kind == "Attended" || f.Kind == "NoShow"))
            .GroupBy(f => new { f.ClinicId, f.Kind })
            .Select(g => new { g.Key.ClinicId, g.Key.Kind, Count = (long)g.Sum(x => x.Count) })
            .ToListAsync(ct);

        long Sum(string clinic, string kind) => facts.Where(f => f.ClinicId == clinic && f.Kind == kind).Sum(f => f.Count);
        var byClinic = facts.Select(f => f.ClinicId).Distinct().Select(c =>
        {
            long booked = Sum(c, "Booked"), attended = Sum(c, "Attended"), noshow = Sum(c, "NoShow");
            var denom = attended + noshow;
            return new NoShowRow(c, booked, attended, noshow, denom == 0 ? 0 : (double)noshow / denom);
        }).OrderBy(r => r.ClinicId).ToList();

        long tBooked = byClinic.Sum(r => r.Booked), tAtt = byClinic.Sum(r => r.Attended), tNo = byClinic.Sum(r => r.NoShow);
        var tDenom = tAtt + tNo;
        return new NoShowReport(tBooked, tAtt, tNo, tDenom == 0 ? 0 : (double)tNo / tDenom, byClinic);
    }

    public async Task<TopCodesReport> TopCodesAsync(string tenant, CodeKind kind, DateOnly from, DateOnly to, int top = 20, CancellationToken ct = default)
    {
        var k = kind.ToString();
        var rows = await db.CodeCounts.AsNoTracking()
            .Where(f => f.TenantId == tenant && f.Kind == k && f.Period >= from && f.Period <= to)
            .GroupBy(f => f.Code)
            .Select(g => new { Code = g.Key, Sum = g.Sum(x => x.Count) })
            .OrderByDescending(r => r.Sum).Take(top).ToListAsync(ct);
        return new TopCodesReport(k, rows.Select(r => new CodeRankRow(r.Code, r.Sum)).ToList());
    }

    public async Task<RejectedRequestsReport> RejectedRequestsAsync(string tenant, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.AuthorizationFacts.AsNoTracking()
            .Where(f => f.TenantId == tenant && f.Outcome == "Rejected" && f.Period >= from && f.Period <= to)
            .GroupBy(f => f.RejectionReasonCode ?? "unspecified")
            .Select(g => new { Reason = g.Key, Count = g.LongCount() })
            .OrderByDescending(r => r.Count).ToListAsync(ct);
        var mapped = rows.Select(r => new RejectionReasonRow(r.Reason, r.Count)).ToList();
        return new RejectedRequestsReport(mapped.Sum(r => r.Count), mapped);
    }

    public async Task<FinancialSummaryReport> FinancialSummaryAsync(string tenant, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.FinancialFacts.AsNoTracking()
            .Where(f => f.TenantId == tenant && f.Period >= from && f.Period <= to)
            .GroupBy(f => f.ServiceLine)
            .Select(g => new { ServiceLine = g.Key, Amount = g.Sum(x => x.Amount), Count = g.Sum(x => x.Count) })
            .OrderByDescending(r => r.Amount).ToListAsync(ct);
        var mapped = rows.Select(r => new FinancialRow(r.ServiceLine, r.Amount, r.Count)).ToList();
        return new FinancialSummaryReport(mapped.Sum(r => r.Amount), mapped.Sum(r => r.Count), mapped);
    }
}
