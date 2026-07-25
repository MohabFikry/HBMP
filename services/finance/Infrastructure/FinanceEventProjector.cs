using System.Globalization;
using Mersal.Finance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Finance.Infrastructure;

/// <summary>A canonical domain event handed to the finance projector: id + type + tenant + a min-necessary field
/// bag carrying ONLY billing codes + quantities + amounts. The subscription consumer builds it from the raw domain
/// event and PROJECTS AWAY any clinical field at the boundary — so a clinical value can never reach the read-model
/// even if a source event carried one. The seam endpoint accepts it directly (deferred fanout bus).</summary>
public sealed record FinanceEvent(
    Guid EventId,
    string EventType,
    string TenantId,
    IReadOnlyDictionary<string, string> Fields,
    DateTimeOffset OccurredAt);

/// <summary>Projects delivery/authorization events into <c>utilization_fact</c> (phase 10.2). Idempotent: dedupe on
/// event id. It reads ONLY the whitelisted keys (service_code, quantities, amounts, provider, coverage) — any
/// clinical key present on the incoming event is ignored, never persisted (finance ≠ diagnosis). It never writes to
/// a source domain.</summary>
public sealed class FinanceEventProjector(FinanceDbContext db, TimeProvider clock)
{
    public async Task<bool> ProjectAsync(FinanceEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (await db.ProcessedEvents.AnyAsync(p => p.EventId == ev.EventId, ct))
            return false;

        var handled = Apply(ev);

        db.ProcessedEvents.Add(new ProcessedEvent { EventId = ev.EventId, EventType = ev.EventType, ConsumedAt = clock.GetUtcNow() });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            if (await db.ProcessedEvents.AsNoTracking().AnyAsync(p => p.EventId == ev.EventId, ct)) return false;
            throw;
        }
        return handled;
    }

    private bool Apply(FinanceEvent ev)
    {
        // Only delivery/authorization events carry billable utilization. A source event's clinical fields (if any)
        // are simply not read here — the whitelist is the projection boundary.
        switch (ev.EventType)
        {
            case "OrderLineConsumed":
                AddUtilization(ev, Field(ev, "serviceLine", "Lab"));
                return true;
            case "RxDispensed":
                AddUtilization(ev, "Pharmacy");
                return true;
            case "ServiceValued":
                AddUtilization(ev, Field(ev, "serviceLine", "General"));
                return true;
            default:
                return false; // unmapped — recorded as processed so it isn't reconsidered
        }
    }

    private void AddUtilization(FinanceEvent ev, string serviceLine)
    {
        var authorized = Int(ev, "authorizedQty", 1);
        var delivered = Int(ev, "deliveredQty", 1);
        var unit = Dec(ev, "unitCost");
        var line = Dec(ev, "lineCost", unit * delivered);
        db.UtilizationFacts.Add(new UtilizationFact
        {
            EventId = ev.EventId,
            TenantId = ev.TenantId,
            BeneficiaryId = Guid.TryParse(Field(ev, "beneficiaryId"), out var bid) ? bid : Guid.Empty,
            CoverageCategory = Field(ev, "coverageCategory", "General"),
            ServiceCode = Field(ev, "serviceCode", Field(ev, "code", "unknown")),
            ServiceLine = serviceLine,
            ProviderId = Guid.TryParse(Field(ev, "providerId"), out var pid) ? pid : null,
            AuthorizedQty = authorized,
            DeliveredQty = delivered,
            UnitCost = unit,
            LineCost = line,
            Period = DateOnly.FromDateTime(ev.OccurredAt.UtcDateTime),
            OccurredAt = ev.OccurredAt,
        });
    }

    private static string Field(FinanceEvent ev, string key, string fallback = "") =>
        ev.Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
    private static int Int(FinanceEvent ev, string key, int fallback = 0) =>
        ev.Fields.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
    private static decimal Dec(FinanceEvent ev, string key, decimal fallback = 0m) =>
        ev.Fields.TryGetValue(key, out var v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
