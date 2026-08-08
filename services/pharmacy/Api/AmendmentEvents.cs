using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// 30.2/30.5 — the events a prescription amendment publishes. The medication twin of orders'
/// <c>AmendmentEvents</c>; see that file for why each destination is chosen and why the fulfilling
/// pharmacy's queue needs no event (it is a live query over <c>prescription_line</c>, so a cancelled line
/// leaves the counter's worklist in the same transaction).
///
/// <para>One thing differs and it matters: <b>the beneficiary is notified first</b>, not the provider. For a
/// chronic script the patient may already be travelling to collect, and design 46 §6 singles that out.</para>
///
/// <para>Like its twin, this class builds PAYLOADS and does not enqueue: <c>OutboxAtomicityTests</c> reads
/// one file at a time, so an enqueue hidden behind a helper reads to it — correctly — as a
/// non-transactional one. The enqueue stays where the transaction is.</para>
/// </summary>
public static class RxAmendmentEvents
{
    // THE PAYLOADS ARE INLINE AT THE CALL SITES TOO, and the domain-payload builders that used to live here
    // are gone. CareFeedEnvelopeArchitectureTests and TenantOnEnvelopeArchitectureTests both read the
    // anonymous object that FOLLOWS the queue argument, to prove `encounterId` and `tenantId` are on the
    // wire. A helper hid both from them — and a mirrored event missing its encounter does not fail, warn or
    // dead-letter: the consumer correctly declines to place the step, acks, and the timeline is quietly
    // missing the order. That is the exact defect this scan was written for. The duplication is the price of
    // the check being able to see what it checks.
    //
    // What remains here is the NOTIFICATION payload, which no scan reads because the notification queue is
    // not tenant-bound in the same way and carries no episode.
    //
    // The names are LITERALS at the enqueue sites, not these constants. CareFeedEnvelopeArchitectureTests
    // scans source for a mirrored event's name beside its payload to prove `encounterId` is on the wire — a
    // step without it has no episode, and a step on the WRONG episode is worse than a missing one. A constant
    // hides the name from that scan, exactly as a helper method hid the enqueue from OutboxAtomicityTests.
    // These remain as the catalogue, and are what non-scanned code should reference.
    public const string LineCancelled = "PrescriptionLineCancelled";
    public const string LineAmended = "PrescriptionLineAmended";

    public const string DomainStream = "pharmacy.events";
    public const string NotificationQueue = "notification.domain-events";

    /// <summary>
    /// 30.4 — an amendment beyond the approved scope republishes <c>RxSubmitted</c>, the event the ORIGINAL
    /// routing used, with <c>requiresApproval</c> set. See orders' AmendmentEvents for why a new event type
    /// would be an orphan or a dead-letter; the same reasoning applies here, and the care timeline's
    /// conditional RxSubmitted mapping already renders it as "sent for approval".
    /// </summary>
    public const string PendingApproval = "RxSubmitted";



    public static object Notification(Prescription rx, PrescriptionLine line, LineAmendmentRecord record) =>
        new
        {
            tenantId = rx.TenantId,
            entityRef = rx.RxNo,
            recipients = Recipients(rx, record),
            fields = new
            {
                rxNo = rx.RxNo,
                drugName = line.DrugName,
                reasonCode = record.ReasonCode,
                amendedAt = record.AmendedAt,
            },
        };

    private static object[] Recipients(Prescription rx, LineAmendmentRecord record)
    {
        var list = new List<object>
        {
            // FIRST, deliberately. For a chronic script the beneficiary may already be on their way to the
            // pharmacy, and a wasted journey is the concrete harm design 46 §6 names.
            new { role = "beneficiary", userId = rx.BeneficiaryId.ToString(), locale = "ar" },
        };

        // The prescriber, only when SOMEBODY ELSE amended their prescription. A confirmation of your own
        // action is noise, and noise is what teaches people to stop reading the channel that also carries
        // "a colleague withdrew your patient's antibiotic".
        if (rx.PrescriberId != record.AmendedBy)
            list.Add(new { role = "doctor", userId = rx.PrescriberId.ToString(), locale = "en" });

        return [.. list];
    }
}
