using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Infrastructure;

/// <summary>
/// Applies upstream patient/policy domain events to the local read models and INVALIDATES the cache
/// for the affected beneficiary, so the next eligibility check recomputes. Idempotent: each event id
/// is recorded in <see cref="ProcessedEvent"/> and re-applied at-most-once.
/// </summary>
public sealed class ProjectionUpdater(EligibilityDbContext db, IEligibilityCache cache, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Handle one event. Returns false if it was a duplicate (already processed).</summary>
    public async Task<bool> ApplyAsync(Guid eventId, string eventType, string payloadJson, CancellationToken ct = default)
    {
        if (await db.ProcessedEvents.AnyAsync(p => p.EventId == eventId, ct)) return false;

        using var doc = JsonDocument.Parse(payloadJson);
        var p = doc.RootElement;

        switch (eventType)
        {
            case "BeneficiaryRegistered": await OnRegistered(p, ct); break;
            case "BeneficiaryActivated": await OnActivated(p, ct); break;
            case "BeneficiaryUpdated": await OnRegistered(p, ct); break;
            case "BeneficiaryStatusChanged": await OnStatusChanged(p, ct); break;
            case "CoverageChanged": await OnCoverageChanged(p, ct); break;
            case "CoverageLimitChanged": await OnLimitChanged(p, ct); break;
            case "PolicyChanged": await OnPolicyChanged(p, ct); break;
            default: break; // unrelated event → still recorded as processed to keep the ledger dense
        }

        db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task OnRegistered(JsonElement p, CancellationToken ct)
    {
        var id = p.GetProperty("beneficiaryId").GetGuid();
        var m = await db.Members.FirstOrDefaultAsync(x => x.BeneficiaryId == id, ct)
                ?? Add(new MemberProjection { BeneficiaryId = id });
        m.GivenName = Str(p, "givenName") ?? m.GivenName;
        m.FamilyName = Str(p, "familyName") ?? m.FamilyName;
        m.MemberNo = Str(p, "memberNo") ?? m.MemberNo;
        m.PrimaryPhone = Str(p, "primaryPhone") ?? m.PrimaryPhone;
        m.Status = Str(p, "status") ?? m.Status;
        ApplyIdentifiers(m, p);
        m.UpdatedAt = clock.GetUtcNow();
        await cache.InvalidateAsync(id, ct);
    }

    private async Task OnActivated(JsonElement p, CancellationToken ct)
    {
        var id = p.GetProperty("beneficiaryId").GetGuid();
        var m = await db.Members.FirstOrDefaultAsync(x => x.BeneficiaryId == id, ct)
                ?? Add(new MemberProjection { BeneficiaryId = id });
        m.Status = "Active";
        m.MemberNo = Str(p, "memberNo") ?? m.MemberNo;
        m.GivenName = Str(p, "givenName") ?? m.GivenName;
        m.FamilyName = Str(p, "familyName") ?? m.FamilyName;
        m.UpdatedAt = clock.GetUtcNow();
        await cache.InvalidateAsync(id, ct);
    }

    private async Task OnStatusChanged(JsonElement p, CancellationToken ct)
    {
        var id = p.GetProperty("beneficiaryId").GetGuid();
        var m = await db.Members.FirstOrDefaultAsync(x => x.BeneficiaryId == id, ct);
        if (m is null) return;
        m.Status = Str(p, "to") ?? m.Status;
        m.UpdatedAt = clock.GetUtcNow();
        await cache.InvalidateAsync(id, ct);
    }

    private async Task OnCoverageChanged(JsonElement p, CancellationToken ct)
    {
        var coverageId = p.GetProperty("coverageId").GetGuid();
        var beneficiaryId = p.GetProperty("beneficiaryId").GetGuid();
        var c = await db.Coverages.FirstOrDefaultAsync(x => x.CoverageId == coverageId, ct)
                ?? Add(new CoverageProjection { CoverageId = coverageId });
        c.BeneficiaryId = beneficiaryId;
        c.BenefitCategory = Str(p, "category") ?? c.BenefitCategory;
        c.Status = Str(p, "status") ?? c.Status;
        c.PolicyNo = Str(p, "policyNo") ?? c.PolicyNo;
        if (p.TryGetProperty("effectiveFrom", out var ef) && ef.ValueKind == JsonValueKind.String)
            c.EffectiveFrom = DateOnly.Parse(ef.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        if (p.TryGetProperty("effectiveTo", out var et) && et.ValueKind == JsonValueKind.String)
            c.EffectiveTo = DateOnly.Parse(et.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        if (p.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            c.LimitsJson = limits.GetRawText();
        c.UpdatedAt = clock.GetUtcNow();
        await cache.InvalidateAsync(beneficiaryId, ct);
    }

    private async Task OnLimitChanged(JsonElement p, CancellationToken ct)
    {
        var coverageId = p.GetProperty("coverageId").GetGuid();
        var c = await db.Coverages.FirstOrDefaultAsync(x => x.CoverageId == coverageId, ct);
        if (c is null) return;
        // Recompute consumed from remaining if the event carries the pair.
        if (p.TryGetProperty("limitType", out var lt) && p.TryGetProperty("limitValue", out var lv)
            && p.TryGetProperty("remaining", out var rem))
        {
            var list = JsonSerializer.Deserialize<List<LimitStateDto>>(c.LimitsJson, Json) ?? [];
            var type = lt.GetString()!;
            var value = lv.GetDecimal();
            var consumed = value - rem.GetDecimal();
            list.RemoveAll(x => string.Equals(x.LimitType, type, StringComparison.OrdinalIgnoreCase));
            list.Add(new LimitStateDto(type, value, consumed));
            c.LimitsJson = JsonSerializer.Serialize(list, Json);
            c.UpdatedAt = clock.GetUtcNow();
        }
        await cache.InvalidateAsync(c.BeneficiaryId, ct);
    }

    private async Task OnPolicyChanged(JsonElement p, CancellationToken ct)
    {
        var policyNo = Str(p, "policyNo");
        var status = Str(p, "status");
        if (policyNo is null) return;
        var affected = await db.Coverages.Where(c => c.PolicyNo == policyNo).ToListAsync(ct);
        foreach (var c in affected)
        {
            // 18.A4: mirror the policy status UNCONDITIONALLY. Only non-Active was written before, so a
            // suspended policy correctly cascaded to its coverages but REACTIVATING it never restored
            // them — the member stayed ineligible with no way back short of a manual DB fix.
            if (status is not null) c.Status = status;
            c.UpdatedAt = clock.GetUtcNow();
            await cache.InvalidateAsync(c.BeneficiaryId, ct);
        }
    }

    private static void ApplyIdentifiers(MemberProjection m, JsonElement p)
    {
        if (!p.TryGetProperty("identifiers", out var ids) || ids.ValueKind != JsonValueKind.Array) return;
        foreach (var i in ids.EnumerateArray())
        {
            var type = Str(i, "type"); var value = Str(i, "value");
            if (type is null || value is null) continue;
            switch (type)
            {
                case "NationalID": m.NationalId = value; break;
                case "Passport": m.Passport = value; break;
                case "RefugeeID": m.RefugeeId = value; break;
                case "UNHCRNo": m.UnhcrNo = value; break;
                default: break;
            }
        }
    }

    private static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private T Add<T>(T entity) where T : class { db.Add(entity); return entity; }
}
