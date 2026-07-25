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

    private static string Csv(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}
