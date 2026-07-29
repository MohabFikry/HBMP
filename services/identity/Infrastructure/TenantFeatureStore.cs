using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Infrastructure;

/// <summary>
/// 21.4 propagation — the issuer's local view of the per-tenant programme switches (design 40 §4/§5).
///
/// Reads feed the `features` claim at token issuance; the single writer is the consumer of
/// TenantFeatureChanged. This is a PROJECTION of admin.tenant_feature and nothing else in identity-service
/// writes it: a switch administered here would give a tenant a token that disagrees with its own
/// administration screen, and the screen is what a human would trust.
/// </summary>
public sealed class TenantFeatureStore(IdentityStoreDbContext db)
{
    /// <summary>
    /// The feature keys switched ON for a tenant. Disabled and absent are deliberately indistinguishable in
    /// the result: both mean "not enabled", and the consumer of this list (<c>ProgramEnablement.IsEnabled</c>)
    /// asks a membership question, so a row that says false is simply not returned.
    /// </summary>
    public async Task<IReadOnlyList<string>> EnabledForAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return [];

        return await db.Database
            .SqlQueryRaw<string>(
                // Ordered so the claim is stable across issuances: an unordered set makes two tokens for the
                // same principal differ byte-for-byte, which turns any diff of them into noise.
                """
                SELECT feature_key AS "Value"
                FROM identity.tenant_feature
                WHERE tenant_id = {0} AND enabled
                ORDER BY feature_key
                """,
                tenantId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Apply one TenantFeatureChanged.
    ///
    /// <para><b>The WHERE clause is the whole point.</b> Delivery is at-least-once and NOT ordered: the broker
    /// may hand us a five-minute-old "off" after the "on" that superseded it. Without the guard, that redelivery
    /// silently switches a live module off for a tenant and nothing looks broken — the projection just quietly
    /// holds an older truth than the source. Comparing the ADMIN-STAMPED changed_at makes the apply
    /// order-independent: a row only ever moves forward in time.</para>
    ///
    /// <para><c>&gt;=</c> rather than <c>&gt;</c> so a genuine re-send of the newest change is still applied —
    /// it is the same value, so writing it costs nothing and refusing it would leave a first delivery that
    /// failed after the dedupe insert stuck forever.</para>
    /// </summary>
    /// <returns>True when the row was written, false when an equal-or-newer state was already held.</returns>
    public async Task<bool> ApplyAsync(
        string tenantId, string featureKey, bool enabled, DateTimeOffset changedAt, Guid eventId,
        CancellationToken ct = default)
    {
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO identity.tenant_feature (tenant_id, feature_key, enabled, changed_at, source_event_id)
            VALUES ({0}, {1}, {2}, {3}, {4})
            ON CONFLICT (tenant_id, feature_key) DO UPDATE
              SET enabled = {2}, changed_at = {3}, source_event_id = {4}
              WHERE identity.tenant_feature.changed_at <= {3}
            """,
            [tenantId, featureKey, enabled, changedAt.UtcDateTime, eventId], ct);

        return affected > 0;
    }
}

/// <summary>
/// Durable dedupe for the consumer (<see cref="Mersal.Events.IProcessedEventStore"/>). In-memory would forget
/// everything on restart, and this store's contract is "have I ever processed this id" — a question a process
/// lifetime cannot answer.
/// </summary>
public sealed class DbProcessedEventStore(IdentityStoreDbContext db) : Mersal.Events.IProcessedEventStore
{
    public async Task<bool> TryBeginAsync(Guid eventId, CancellationToken ct = default)
    {
        // ON CONFLICT DO NOTHING makes the claim atomic: two consumers racing the same redelivery see exactly
        // one insert, so the handler runs once. A SELECT-then-INSERT would let both through.
        var inserted = await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO identity.processed_event (event_id) VALUES ({0}) ON CONFLICT (event_id) DO NOTHING",
            [eventId], ct);
        return inserted > 0;
    }
}
