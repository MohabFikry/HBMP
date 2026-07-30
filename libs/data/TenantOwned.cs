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

    /// <summary>21.5 — attribution columns stamped from the ACTIVE MEMBERSHIP (design 40 §6).</summary>
    public const string CreatedByProperty = "CreatedBy";
    public const string UpdatedByProperty = "UpdatedBy";

    private void Stamp(DbContext? db)
    {
        if (db is null) return;
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            // 24.x — FAIL RATHER THAN WRITE A ROW THAT BELONGS TO NOBODY.
            //
            // This guard used to be the whole story: "if there is a tenant, stamp it" — and if there was
            // not, it silently did nothing and the entity's own `= ""` default was persisted. The row then
            // belonged to no tenant: invisible to every real one (because `tenant_id = <real>` never
            // matches) and visible to anything binding an empty tenant. Measured on the dev database,
            // 1,191 rows across seven tables had landed that way. Nothing failed at the time; the data
            // simply stopped existing for the application that wrote it.
            //
            // A write with no tenant to stamp is a bug in the caller — a handler running outside the RLS
            // middleware's path prefix, or a background worker that did not bind RlsContext the way the
            // event consumers do. Throwing names it at the point of the mistake instead of leaving an
            // orphan row for someone to find months later with no way to tell whose it was.
            if (entry.State == EntityState.Added)
            {
                if (!string.IsNullOrEmpty(context.TenantId))
                    StampIfEmpty(entry, ColumnProperty, context.TenantId);
                else if (IsWritableString(entry, ColumnProperty)
                         && string.IsNullOrEmpty(entry.Property(ColumnProperty).CurrentValue as string))
                    throw new InvalidOperationException(
                        $"{entry.Metadata.ClrType.Name} is tenant-scoped and is being inserted with no tenant: " +
                        "RlsContext.TenantId is empty and the entity does not set TenantId itself. The row " +
                        "would belong to no tenant — invisible to every real one and visible to any session " +
                        "binding an empty tenant. Bind the tenant (UseHbmpRls for requests, or set " +
                        "RlsContext.TenantId explicitly in a background worker) before saving.");
            }

            // 21.5 — AMBIENT ATTRIBUTION. Stamped here rather than in each handler because an attribution
            // gap is created by OMISSION: the endpoint that forgets is the one nobody wrote a test for, and
            // the missing name only becomes visible during the incident review that needed it. The
            // membership, not the raw user id — the same person may act in two organisations, and
            // "u-1234 changed this" cannot say which hat they were wearing.
            if (string.IsNullOrEmpty(context.MembershipId)) continue;

            if (entry.State == EntityState.Added)
                StampIfEmpty(entry, CreatedByProperty, context.MembershipId);

            // updated_by is overwritten, not filled-if-empty: it names whoever made THIS change, so
            // carrying the previous editor forward would misattribute every subsequent edit.
            StampAlways(entry, UpdatedByProperty, context.MembershipId);
        }
    }

    private static void StampIfEmpty(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string name, string value)
    {
        if (!IsWritableString(entry, name)) return;
        var member = entry.Property(name);
        if (string.IsNullOrEmpty(member.CurrentValue as string)) member.CurrentValue = value;
    }

    private static void StampAlways(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string name, string value)
    {
        if (!IsWritableString(entry, name)) return;
        entry.Property(name).CurrentValue = value;
    }

    private static bool IsWritableString(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string name)
    {
        var prop = entry.Metadata.FindProperty(name);
        return prop is not null && prop.ClrType == typeof(string);
    }
}
