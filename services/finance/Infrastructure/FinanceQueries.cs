using System.Globalization;
using System.Text;
using Mersal.Finance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Finance.Infrastructure;

/// <summary>Read-side aggregates over the finance read-model (phase 10.2). All returns are
/// <see cref="IFinanceProjection"/> DTOs — billing codes, quantities, amounts, masked-min PII only. There is no
/// clinical filter or column anywhere in these queries.</summary>
public sealed class FinanceQueries(FinanceDbContext db)
{
    public async Task<UtilizationView> UtilizationAsync(
        string tenantId, DateOnly from, DateOnly to,
        string? category, Guid? providerId, Guid? beneficiaryId, CancellationToken ct = default)
    {
        var q = db.UtilizationFacts.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.Period >= from && f.Period <= to);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(f => f.CoverageCategory == category);
        if (providerId is not null) q = q.Where(f => f.ProviderId == providerId);
        if (beneficiaryId is not null) q = q.Where(f => f.BeneficiaryId == beneficiaryId);

        // Group in the DB to anonymous, then map (EF can't project into a positional record in GroupBy).
        var grouped = await q
            .GroupBy(f => new { f.ServiceCode, f.ServiceLine, f.CoverageCategory, f.ProviderId })
            .Select(g => new
            {
                g.Key.ServiceCode, g.Key.ServiceLine, g.Key.CoverageCategory, g.Key.ProviderId,
                Authorized = g.Sum(x => x.AuthorizedQty),
                Delivered = g.Sum(x => x.DeliveredQty),
                Spend = g.Sum(x => x.LineCost),
            })
            .OrderByDescending(x => x.Spend)
            .ToListAsync(ct);

        var rows = grouped.Select(g => new UtilizationRow(
            g.ServiceCode, g.ServiceLine, g.CoverageCategory,
            g.ProviderId?.ToString(), g.Authorized, g.Delivered, g.Spend)).ToList();
        return UtilizationView.From(rows);
    }

    public async Task<FinancialSummaryView> SummaryAsync(
        string tenantId, DateOnly from, DateOnly to, string dimension, CancellationToken ct = default)
    {
        var q = db.UtilizationFacts.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.Period >= from && f.Period <= to);

        // dimension ∈ { serviceline, category, provider } — billing dimensions only.
        var grouped = dimension.ToLowerInvariant() switch
        {
            "category" => await q.GroupBy(f => f.CoverageCategory)
                .Select(g => new { Key = g.Key, Delivered = g.Sum(x => x.DeliveredQty), Spend = g.Sum(x => x.LineCost) }).ToListAsync(ct),
            "provider" => await q.GroupBy(f => f.ProviderId)
                .Select(g => new { Key = g.Key.ToString() ?? "unknown", Delivered = g.Sum(x => x.DeliveredQty), Spend = g.Sum(x => x.LineCost) }).ToListAsync(ct),
            _ => await q.GroupBy(f => f.ServiceLine)
                .Select(g => new { Key = g.Key, Delivered = g.Sum(x => x.DeliveredQty), Spend = g.Sum(x => x.LineCost) }).ToListAsync(ct),
        };

        var buckets = grouped
            .OrderByDescending(g => g.Spend)
            .Select(g => new SummaryBucket(dimension, g.Key ?? "unknown", g.Delivered, g.Spend))
            .ToList();
        return new FinancialSummaryView(dimension, buckets, buckets.Sum(b => b.Spend));
    }

    /// <summary>
    /// Settlements for export, newest first, capped identically to the list endpoint.
    /// </summary>
    /// <remarks>
    /// The export used to run <see cref="UtilizationAsync"/> whatever report was asked for, so this query did
    /// not exist and "export the settlements" produced utilization rows in a file named after settlements.
    /// </remarks>
    public async Task<IReadOnlyList<SettlementView>> SettlementsForExportAsync(
        string tenantId, DateOnly from, DateOnly to, Guid? providerId, CancellationToken ct = default)
    {
        var q = db.Settlements.AsNoTracking().Include(s => s.Lines).Where(s => s.TenantId == tenantId);
        if (providerId is not null) q = q.Where(s => s.ProviderId == providerId);

        // OVERLAP, not containment — and said out loud because the two differ exactly on the settlements a
        // finance question is most often about, the ones spanning a month boundary. An export of "July" that
        // silently dropped a 25-Jun–5-Jul settlement would under-state the period by however much of it fell
        // inside. The list screen deliberately has no period control for this reason (design 49 §4); an
        // export names its window in the filename and the audit record, so here the choice can be stated.
        q = q.Where(s => s.PeriodStart <= to && s.PeriodEnd >= from);

        var rows = await q.OrderByDescending(s => s.CreatedAt).Take(ExportCap).ToListAsync(ct);
        return [.. rows.Select(SettlementView.From)];
    }

    /// <summary>The same 100 the list endpoint caps at. An export that returned more than the screen can show
    /// would answer a different question than the one the operator was looking at.</summary>
    public const int ExportCap = 100;

    /// <summary>Render a projection to CSV for export. PII is already masked-min in the DTOs (no beneficiary name,
    /// provider as a reference id). Returns the CSV + row count for the audit event.</summary>
    public static (string Csv, int Rows) ToCsv(UtilizationView view)
    {
        var sb = new StringBuilder();
        sb.AppendLine("service_code,service_line,coverage_category,provider_ref,authorized_qty,delivered_qty,spend");
        foreach (var r in view.Rows)
            sb.Append(Csv(r.ServiceCode)).Append(',').Append(Csv(r.ServiceLine)).Append(',')
              .Append(Csv(r.CoverageCategory)).Append(',').Append(Csv(r.ProviderRef ?? "")).Append(',')
              .Append(r.AuthorizedQty).Append(',').Append(r.DeliveredQty).Append(',')
              .Append(r.Spend.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();
        return (sb.ToString(), view.Rows.Count);
    }

    /// <summary>
    /// Settlements to CSV — one row per settlement LINE, with the header's facts repeated on each.
    /// </summary>
    /// <remarks>
    /// <para>Line-grained rather than settlement-grained because the question this file is opened to answer
    /// is "what are we paying this provider for", and a total tells nobody that. The settlement number,
    /// provider and period repeat on every row so each line stands on its own in a spreadsheet after a sort.</para>
    /// <para><c>price_source</c> is a column. A line priced at <see cref="SettlementPriceSource.ObservedFloor"/>
    /// is not the same kind of number as one the contract's price book named, and a file that renders them
    /// identically has thrown away the distinction the domain went to the trouble of recording.</para>
    /// <para>The row count is LINES, not settlements — it is what the audit event reports, and it must count
    /// the same things the file contains.</para>
    /// </remarks>
    public static (string Csv, int Rows) ToCsv(IReadOnlyList<SettlementView> settlements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("settlement_no,provider_ref,period_start,period_end,status,currency,"
            + "service_code,service_line,delivered_qty,agreed_unit_price,line_total,price_source");
        var rows = 0;
        foreach (var s in settlements)
        {
            foreach (var l in s.Lines)
            {
                sb.Append(Csv(s.SettlementNo)).Append(',').Append(Csv(s.ProviderRef)).Append(',')
                  .Append(s.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(s.Status)).Append(',').Append(Csv(s.CurrencyCode)).Append(',')
                  .Append(Csv(l.ServiceCode)).Append(',').Append(Csv(l.ServiceLine)).Append(',')
                  .Append(l.DeliveredQty).Append(',')
                  .Append(l.AgreedUnitPrice.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                  .Append(l.LineTotal.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(l.PriceSource)).AppendLine();
                rows++;
            }
        }
        return (sb.ToString(), rows);
    }

    /// <summary>The roll-up to CSV — the donor/leadership file. The dimension is a column rather than only a
    /// filename, so a file that has been renamed still says what it grouped by.</summary>
    public static (string Csv, int Rows) ToCsv(FinancialSummaryView view)
    {
        var sb = new StringBuilder();
        sb.AppendLine("dimension,key,delivered_qty,spend");
        foreach (var b in view.Buckets)
            sb.Append(Csv(view.Dimension)).Append(',').Append(Csv(b.Key)).Append(',')
              .Append(b.DeliveredQty).Append(',')
              .Append(b.Spend.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();
        return (sb.ToString(), view.Buckets.Count);
    }

    private static string Csv(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}
