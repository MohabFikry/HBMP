using Mersal.Audit.Client;
using Mersal.Audit.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Audit.Infrastructure;

/// <summary>
/// PostgreSQL-backed <see cref="IAuditEventStore"/>. INSERT + SELECT only — the DB role has no
/// UPDATE/DELETE grant within retention (enforced by the SQL migration), so this class exposes
/// no update/delete path either.
/// </summary>
public sealed class PostgresAuditEventStore(AuditDbContext db) : IAuditEventStore
{
    public async Task<string?> GetLastRecordHashAsync(string partitionKey, CancellationToken ct = default)
    {
        return await db.AuditEvents
            .Where(x => x.PartitionKey == partitionKey)
            .OrderByDescending(x => x.Seq)
            .Select(x => x.RecordHash)
            .FirstOrDefaultAsync(ct);
    }

    public Task<bool> ExistsAsync(Guid auditEventId, CancellationToken ct = default) =>
        db.AuditEvents.AsNoTracking().AnyAsync(x => x.AuditEventId == auditEventId, ct);

    public async Task AppendAsync(AuditEvent chained, CancellationToken ct = default)
    {
        var row = AuditEventRow.FromDomain(chained, AuditPartition.KeyFor(chained.OccurredAt));
        db.AuditEvents.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> ReadPartitionAsync(string partitionKey, CancellationToken ct = default)
    {
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(x => x.PartitionKey == partitionKey)
            .OrderBy(x => x.Seq)
            .ToListAsync(ct);
        return rows.ConvertAll(r => r.ToDomain());
    }
}
