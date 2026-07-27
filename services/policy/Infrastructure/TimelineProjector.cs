using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// Phase 19.3c — writes projected entries, idempotently, and rebuilds the whole projection from source.
///
/// <para>Nothing in the domain calls this as part of doing its work. It consumes events that already exist,
/// which is what keeps the timeline from becoming a second log that drifts from the audit trail.</para>
/// </summary>
public sealed class TimelineProjector(PolicyDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Project a batch. Returns how many entries were NEW.
    ///
    /// <para>Idempotent twice over: the entry id is derived from the source event id, and the unique index on
    /// <c>source_event_id</c> refuses a duplicate anyway. Re-delivering the same event is therefore a no-op
    /// rather than a duplicated line in someone's history — which matters because at-least-once delivery makes
    /// re-delivery normal, not exceptional.</para>
    /// </summary>
    public async Task<int> ProjectAsync(
        IEnumerable<TimelineSource> sources, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var batch = sources.ToList();
        if (batch.Count == 0) return 0;

        var ids = batch.Select(s => s.EventId).ToList();
        var already = await db.TimelineEntries.AsNoTracking()
            .Where(e => ids.Contains(e.SourceEventId))
            .Select(e => e.SourceEventId)
            .ToListAsync(ct);
        var seen = already.ToHashSet();

        var now = clock.GetUtcNow();
        var fresh = batch
            .Where(s => seen.Add(s.EventId))   // also dedupes WITHIN the batch
            .Select(s => TimelineProjection.Project(s, tenantId, now))
            .ToList();
        if (fresh.Count == 0) return 0;

        db.TimelineEntries.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    /// <summary>
    /// Rebuild the whole projection for a tenant from source.
    ///
    /// <para>The append-only trigger refuses every delete EXCEPT inside a declared rebuild, signalled by the
    /// session GUC <c>app.timeline_rebuild</c>. The asymmetry is deliberate: discarding all derived data and
    /// re-projecting it is safe in a way that quietly removing one inconvenient line is not, and requiring an
    /// explicit flag means a rebuild is a decision somebody made rather than something a stray DELETE achieves.</para>
    ///
    /// <para>Because <see cref="TimelineProjection.EntryIdFor"/> is a hash of the source event id and the diff
    /// serializer orders its keys, a rebuild produces BYTE-IDENTICAL rows — so "the rebuild worked" is
    /// verifiable by comparison rather than by eye.</para>
    /// </summary>
    public async Task<int> RebuildAsync(
        IEnumerable<TimelineSource> allSources, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allSources);
        // SET LOCAL is scoped to a TRANSACTION, so the clear-out runs inside an explicit one. Outside a
        // transaction SET LOCAL is silently a no-op — the flag would never reach the trigger and every rebuild
        // would fail with the append-only error, which is exactly what the guard is supposed to do to
        // everything that is not a declared rebuild.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL app.timeline_rebuild = 'on'", ct);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.entity_timeline WHERE tenant_id = {0}", [tenantId], ct);
            await tx.CommitAsync(ct);
            // The flag dies with the transaction — nothing running later on this connection inherits it.
        }
        return await ProjectAsync(allSources, tenantId, ct);
    }
}
