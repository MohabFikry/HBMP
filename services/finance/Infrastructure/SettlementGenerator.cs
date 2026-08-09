using System.Globalization;
using Mersal.Finance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Finance.Infrastructure;

/// <summary>Generates a provider settlement for a period from <c>utilization_fact</c> × the provider's agreed
/// contract prices (READ from provider-service). Delivered quantities are grouped by billing service code; each
/// line is priced from the in-effect price book. A code the contract does not price falls back to the LOWEST
/// unit cost observed for it in the period — never the average, which one mispriced small delivery lifts for
/// everything — and the line records that it did, so the reviewer issuing the draft can see which prices have
/// no tariff behind them. Deterministic totals. No clinical data participates.</summary>
public sealed class SettlementGenerator(FinanceDbContext db, IContractPriceProvider prices, SettlementNoIssuer numbers, TimeProvider clock)
{
    public async Task<Settlement> GenerateAsync(
        string tenantId, Guid providerId, DateOnly periodStart, DateOnly periodEnd, string? createdBy, string? bearerToken, CancellationToken ct = default)
    {
        var facts = await db.UtilizationFacts.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.ProviderId == providerId
                        && f.Period >= periodStart && f.Period <= periodEnd)
            .ToListAsync(ct);

        var grouped = facts
            .GroupBy(f => f.ServiceCode)
            .Select(g => new
            {
                ServiceCode = g.Key,
                ServiceLine = g.Select(x => x.ServiceLine).FirstOrDefault() ?? "General",
                Delivered = g.Sum(x => x.DeliveredQty),
                // The FLOOR, not the average. An average is the statistic a single mispriced small delivery
                // moves most, and it moves it upward — see SettlementPriceSource.ObservedFloor.
                ObservedFloor = g.Select(x => x.UnitCost).DefaultIfEmpty(0m).Min(),
            })
            .OrderBy(g => g.ServiceCode, StringComparer.Ordinal)
            .ToList();

        var book = await prices.GetPriceBookAsync(providerId, periodEnd, bearerToken, ct) ?? ContractPriceBook.Empty();
        var now = clock.GetUtcNow();
        var settlement = new Settlement
        {
            SettlementId = Guid.NewGuid(),
            SettlementNo = await numbers.NextAsync(now.Year, ct),
            TenantId = tenantId,
            ProviderId = providerId,
            ContractId = book.ContractId == Guid.Empty ? null : book.ContractId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CurrencyCode = book.CurrencyCode,
            Status = SettlementStatus.Draft,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var g in grouped)
        {
            var priced = book.TryPrice(g.ServiceCode, out var agreed);
            var unit = priced ? agreed : decimal.Round(g.ObservedFloor, 2, MidpointRounding.ToEven);
            settlement.Lines.Add(new SettlementLine
            {
                SettlementLineId = Guid.NewGuid(),
                SettlementId = settlement.SettlementId,
                ServiceCode = g.ServiceCode,
                ServiceLine = g.ServiceLine,
                DeliveredQty = g.Delivered,
                AgreedUnitPrice = unit,
                LineTotal = unit * g.Delivered,
                PriceSource = priced ? SettlementPriceSource.Contract : SettlementPriceSource.ObservedFloor,
            });
        }
        settlement.Total = settlement.Lines.Sum(l => l.LineTotal);
        return settlement;
    }
}

/// <summary>Issues the next monotonic settlement number for a year (atomic upsert on settlement_seq).</summary>
public sealed class SettlementNoIssuer(FinanceDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO finance.settlement_seq(year, last_value) VALUES (@y, 1)
                                ON CONFLICT (year) DO UPDATE SET last_value = finance.settlement_seq.last_value + 1
                                RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return SettlementNo.Format(year, seq);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}
