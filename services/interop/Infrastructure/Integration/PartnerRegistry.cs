using Mersal.Interop.Domain.Integration;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Interop.Infrastructure.Integration;

/// <summary>
/// The DB-backed partner registry (13.2). The ONLY path to enablement runs through the <see cref="DpiaGate"/>:
/// <see cref="TryEnableAsync"/> refuses (no state change) unless a DPIA sign-off AND a data-sharing agreement
/// reference both exist. A DB CHECK constraint backs this so an out-of-band UPDATE can't enable an integration
/// without both artifacts either. Enablement attempts are audited by the caller (admin endpoint).
/// </summary>
public sealed class DbExternalPartnerRegistry(InteropDbContext db) : IExternalPartnerRegistry
{
    public async Task<IReadOnlyList<PartnerDescriptor>> ListAsync(CancellationToken ct = default) =>
        (await db.Partners.AsNoTracking().OrderBy(p => p.PartnerId).ToListAsync(ct)).Select(ToDescriptor).ToList();

    public async Task<PartnerDescriptor?> GetAsync(string partnerId, CancellationToken ct = default)
    {
        var row = await db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.PartnerId == partnerId, ct);
        return row is null ? null : ToDescriptor(row);
    }

    public async Task UpsertAsync(PartnerDescriptor d, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(d);
        var row = await db.Partners.FirstOrDefaultAsync(p => p.PartnerId == d.PartnerId, ct);
        if (row is null)
        {
            row = new IntegrationPartnerRecord { PartnerId = d.PartnerId };
            db.Partners.Add(row);
        }
        row.Name = d.Name;
        row.Direction = d.Direction.ToString();
        row.Transport = d.Transport.ToString();
        row.Status = d.Status.ToString();
        row.Dpia = d.Dpia.ToString();
        row.DataSharingAgreementRef = d.DataSharingAgreementRef;
        row.CrossBorder = d.CrossBorder;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<GateOutcome> TryEnableAsync(string partnerId, CancellationToken ct = default)
    {
        var row = await db.Partners.FirstOrDefaultAsync(p => p.PartnerId == partnerId, ct);
        if (row is null) return GateOutcome.Deny("unknown-partner", $"No partner '{partnerId}' is registered.");

        var gate = DpiaGate.CanEnable(ToDescriptor(row));
        if (!gate.Allowed) return gate; // refused, no state change

        row.Status = IntegrationStatus.Enabled.ToString();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return gate;
    }

    public async Task DisableAsync(string partnerId, CancellationToken ct = default)
    {
        var row = await db.Partners.FirstOrDefaultAsync(p => p.PartnerId == partnerId, ct);
        if (row is null) return;
        row.Status = IntegrationStatus.Disabled.ToString();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static PartnerDescriptor ToDescriptor(IntegrationPartnerRecord r) => new()
    {
        PartnerId = r.PartnerId,
        Name = r.Name,
        Direction = Enum.Parse<PartnerDirection>(r.Direction),
        Transport = Enum.Parse<PartnerTransport>(r.Transport),
        Status = Enum.Parse<IntegrationStatus>(r.Status),
        Dpia = Enum.Parse<DpiaStatus>(r.Dpia),
        DataSharingAgreementRef = r.DataSharingAgreementRef,
        CrossBorder = r.CrossBorder,
    };
}
