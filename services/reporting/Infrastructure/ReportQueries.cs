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

        var names = await ClinicNamesAsync(tenant, rows.Select(r => r.ClinicId), ct);
        return new ClinicWorkloadReport(rows
            .Select(r => new WorkloadRow(r.ClinicId, NameEn(names, r.ClinicId), NameAr(names, r.ClinicId), r.Period, r.Sum))
            .ToList());
    }

    /// <summary>
    /// Bilingual clinic names for a set of location ids, from the dimension table.
    /// </summary>
    /// <remarks>
    /// <para>One lookup for the whole result rather than a join, because <c>EncounterFact.ClinicId</c> is TEXT
    /// and <c>DimensionLabel.DimensionId</c> is a uuid — the fact table stores whatever the publisher sent, so
    /// it can legitimately hold a value that is not a location at all. Parsing in memory keeps an unparseable
    /// id as an unlabelled row instead of failing the whole report, which is the behaviour a supervisor wants:
    /// one nameless clinic is a gap, a blank page is an outage.</para>
    /// </remarks>
    private async Task<Dictionary<Guid, DimensionLabel>> ClinicNamesAsync(
        string tenant, IEnumerable<string> clinicIds, CancellationToken ct)
    {
        var ids = clinicIds
            .Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null)
            .Where(g => g is not null).Select(g => g!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await db.DimensionLabels.AsNoTracking()
            .Where(d => d.TenantId == tenant && d.Kind == "branch" && ids.Contains(d.DimensionId))
            .ToDictionaryAsync(d => d.DimensionId, ct);
    }

    private static string? NameEn(Dictionary<Guid, DimensionLabel> names, string clinicId) =>
        Guid.TryParse(clinicId, out var g) && names.TryGetValue(g, out var l) ? l.LabelEn : null;

    private static string? NameAr(Dictionary<Guid, DimensionLabel> names, string clinicId) =>
        Guid.TryParse(clinicId, out var g) && names.TryGetValue(g, out var l) ? l.LabelAr : null;

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
            return new NoShowRow(c, null, null, booked, attended, noshow, denom == 0 ? 0 : (double)noshow / denom);
        }).OrderBy(r => r.ClinicId).ToList();

        long tBooked = byClinic.Sum(r => r.Booked), tAtt = byClinic.Sum(r => r.Attended), tNo = byClinic.Sum(r => r.NoShow);
        var tDenom = tAtt + tNo;

        var names = await ClinicNamesAsync(tenant, byClinic.Select(r => r.ClinicId), ct);
        var labelled = byClinic
            .Select(r => r with { ClinicNameEn = NameEn(names, r.ClinicId), ClinicNameAr = NameAr(names, r.ClinicId) })
            .ToList();
        return new NoShowReport(tBooked, tAtt, tNo, tDenom == 0 ? 0 : (double)tNo / tDenom, labelled);
    }

    /// <summary>
    /// The authorizations behind an SLA-breach count — still pending, already past their due time.
    /// </summary>
    /// <remarks>
    /// Read from <c>pending_authorization</c> rather than from the decided facts on purpose: a breach a
    /// supervisor can still do something about is one that has not been decided yet. A decided-but-breached
    /// authorization is history and is already counted by the TAT report; this list is a worklist.
    /// </remarks>
    public async Task<SlaBreachReport> SlaBreachesAsync(string tenant, int top = 100, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var pending = await db.PendingAuthorizations.AsNoTracking()
            .Where(p => p.TenantId == tenant)
            .ToListAsync(ct);

        var breached = pending
            .Where(p => p.SlaBreached || (p.SlaDueAt is { } due && due < now))
            .Select(p => new SlaBreachRow(
                p.AuthNo ?? p.AuthorizationId.ToString(), p.Priority, p.Status,
                AgeBuckets.Of(now - p.SubmittedAt),
                (long)(now - p.SubmittedAt).TotalSeconds,
                p.ReviewerId))
            // Oldest first: the queue a supervisor works is the one that has waited longest, and a list
            // ordered by anything else buries the case that has been waiting three days under today's.
            .OrderByDescending(r => r.AgeSeconds)
            .ToList();

        return new SlaBreachReport(breached.Count, breached.Take(top).ToList());
    }

    /// <summary>
    /// Claim outcomes and what they cost. Financial zone.
    /// </summary>
    /// <remarks>
    /// Built from the reporting read model rather than by calling claims-service, because the Medical
    /// Director holds <c>reporting:read-financial</c> and holds neither <c>claims:read</c> nor
    /// <c>claims:reconcile</c> — and that is the right boundary rather than an obstacle to route around. A
    /// supervisor needs the SHAPE of what was claimed and denied; opening a claimant's file is the claims
    /// officer's authority, and widening an operational scope to satisfy an analytical need is how the two
    /// stop being distinguishable.
    /// </remarks>
    public async Task<ClaimsSummaryReport> ClaimsSummaryAsync(string tenant, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var costs = await db.CostFacts.AsNoTracking()
            .Where(c => c.TenantId == tenant && c.Period >= from && c.Period <= to)
            .Select(c => new { c.ClaimedAmount, c.ApprovedAmount, c.AdjustedAmount, c.ClaimCount })
            .ToListAsync(ct);

        // The outcome is derived from the money rather than stored, because the terminal event's status is
        // not on the cost fact: nothing approved is a denial, something-but-not-everything is a partial.
        // Reading it this way means the three buckets always sum to the decided total.
        static string Outcome(decimal claimed, decimal approved) =>
            approved <= 0m ? "Denied" : approved >= claimed ? "Approved" : "PartiallyApproved";

        var byOutcome = costs
            .GroupBy(c => Outcome(c.ClaimedAmount, c.ApprovedAmount))
            .Select(g => new ClaimOutcomeRow(g.Key, g.Sum(x => (long)x.ClaimCount)))
            .OrderBy(r => r.Outcome, StringComparer.Ordinal)
            .ToList();

        var financial = await FinancialSummaryAsync(tenant, from, to, ct);
        var denials = await RejectedRequestsAsync(tenant, from, to, ct);

        return new ClaimsSummaryReport(
            byOutcome.Sum(r => r.Count),
            costs.Sum(c => c.ApprovedAmount - c.AdjustedAmount),
            byOutcome,
            financial.ByServiceLine,
            denials.ByReason.Take(10).ToList());
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
