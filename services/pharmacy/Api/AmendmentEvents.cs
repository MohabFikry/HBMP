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
/// </summary>
public static class RxAmendmentEvents
{
    public const string LineCancelled = "PrescriptionLineCancelled";
    public const string LineAmended = "PrescriptionLineAmended";

    public static async Task PublishCancelledAsync(
        IOutbox outbox, Prescription rx, PrescriptionLine line, LineAmendmentRecord record, CancellationToken ct)
    {
        await outbox.EnqueueAsync(LineCancelled, "pharmacy.events", Payload(rx, line, record, null), ct);
        await NotifyAsync(outbox, rx, line, record, LineCancelled, ct);
    }

    public static async Task PublishAmendedAsync(
        IOutbox outbox, Prescription rx, PrescriptionLine line, LineAmendmentRecord record, CancellationToken ct)
    {
        await outbox.EnqueueAsync(LineAmended, "pharmacy.events", Payload(rx, line, record, record.NewLineId), ct);
        await NotifyAsync(outbox, rx, line, record, LineAmended, ct);
    }

    /// <summary><c>encounterId</c> is mandatory on anything the care feed mirrors — a step without it has no
    /// episode, and a step on the wrong episode is worse than a missing one.</summary>
    private static object Payload(
        Prescription rx, PrescriptionLine line, LineAmendmentRecord record, Guid? newLineId) => new
    {
        tenantId = rx.TenantId,
        prescriptionId = rx.PrescriptionId,
        rx.RxNo,
        encounterId = rx.EncounterId,
        beneficiaryId = rx.BeneficiaryId,
        prescriptionLineId = line.PrescriptionLineId,
        newLineId,
        drugId = line.DrugId,
        drugName = line.DrugName,
        reasonCode = record.ReasonCode,
        reasonText = record.ReasonText,
        amendedByUserId = record.AmendedBy,
        amendedAt = record.AmendedAt,
    };

    private static async Task NotifyAsync(
        IOutbox outbox, Prescription rx, PrescriptionLine line, LineAmendmentRecord record, string eventType,
        CancellationToken ct) =>
        await outbox.EnqueueAsync(eventType, "notification.domain-events", new
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
        }, ct);

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
