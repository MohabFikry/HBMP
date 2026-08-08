using System.Text.Json.Nodes;
using Mersal.Events;
using Mersal.Interop.Domain.Integration;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Interop.Infrastructure.Integration;

/// <summary>
/// The inbound ingestion pipeline (13.2). A partner message ALWAYS lands in staging first; then:
///  • the partner must be registered AND <c>Enabled</c> (DPIA-gated) — otherwise the message is quarantined;
///  • the partner's ACL (<see cref="IInboundIntegrationAdapter"/>) translates it to internal domain events;
///  • on success the events are enqueued to the OUTBOX (same transaction as the staging write) and nothing
///    touches a core table directly; on failure the message stays quarantined with a reason.
/// This is the anti-corruption boundary — the core never sees the partner's schema.
/// </summary>
public sealed class InboundIngestionService(
    InteropDbContext db,
    IExternalPartnerRegistry registry,
    IOutbox outbox,
    IEnumerable<IInboundIntegrationAdapter> adapters,
    TimeProvider clock)
{
    private readonly Dictionary<string, IInboundIntegrationAdapter> _adapters =
        adapters.ToDictionary(a => a.PartnerId, StringComparer.Ordinal);

    public async Task<AclResult> IngestAsync(InboundMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        AclResult result;
        var partner = await registry.GetAsync(message.PartnerId, ct);
        if (partner is null)
            result = AclResult.Quarantine($"unknown partner '{message.PartnerId}'");
        else if (partner.Status != IntegrationStatus.Enabled)
            result = AclResult.Quarantine($"partner '{message.PartnerId}' is not enabled (DPIA gate)");
        else if (!_adapters.TryGetValue(message.PartnerId, out var adapter))
            result = AclResult.Quarantine($"no inbound adapter registered for '{message.PartnerId}'");
        else
            result = adapter.Translate(message);

        // Persist the staging row + (on success) the internal events on the SAME transaction (durable outbox).
        // The comment was here before the transaction was: EfOutbox.EnqueueAsync calls its own SaveChanges, so
        // a message that translated to three events wrote three commits, and the staging row rode along with
        // whichever happened to flush first. A crash mid-way left the message recorded as Mapped with only
        // some of its events staged — and the staging row is the only record that the rest were owed.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Staging.Add(new InboundStagingRecord
        {
            StagingId = Guid.NewGuid(),
            PartnerId = message.PartnerId,
            Format = message.Format,
            Body = message.Body,
            State = result.IsMapped ? "Mapped" : "Quarantined",
            Reason = result.QuarantineReason,
            ReceivedAt = clock.GetUtcNow(),
        });

        if (result.IsMapped)
            foreach (var evt in result.Mapped!)
                await outbox.EnqueueAsync(evt.Type, "interop.inbound", JsonNode.Parse(evt.PayloadJson), ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Quarantined messages awaiting review (never applied to core tables).</summary>
    public async Task<IReadOnlyList<InboundStagingRecord>> QuarantinedAsync(string partnerId, CancellationToken ct = default) =>
        await db.Staging.AsNoTracking()
            .Where(s => s.PartnerId == partnerId && s.State == "Quarantined")
            .OrderByDescending(s => s.ReceivedAt).ToListAsync(ct);
}
