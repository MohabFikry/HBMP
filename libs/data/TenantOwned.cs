using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Mersal.Data;

/// <summary>Stamps the current request's tenant onto every inserted entity that maps a <c>TenantId</c>
/// property (audit H1 / ADR-0011), so no create path can forget it and the inserted <c>tenant_id</c> always
/// equals the RLS GUC the connection carries (satisfying the policy's insert check). Metadata-driven — an
/// entity only needs a mapped <c>TenantId</c> string column; no marker interface, so dependency-light Domain
/// projects need not reference this library. Only fills an unset value, so an explicitly-set tenant (e.g. a
/// background consumer stamping from an event) is preserved. The column also carries a DB DEFAULT of the sole
/// tenant, so raw/non-EF inserts and historical rows remain valid.</summary>
public sealed class TenantStampingInterceptor(RlsContext context) : SaveChangesInterceptor
{
    public const string ColumnProperty = "TenantId";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    private void Stamp(DbContext? db)
    {
        if (db is null || string.IsNullOrEmpty(context.TenantId)) return;
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added) continue;
            var prop = entry.Metadata.FindProperty(ColumnProperty);
            if (prop is null || prop.ClrType != typeof(string)) continue;
            var member = entry.Property(ColumnProperty);
            if (string.IsNullOrEmpty(member.CurrentValue as string))
                member.CurrentValue = context.TenantId;
        }
    }
}
