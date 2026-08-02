using System.Text.Json;
using Mersal.Emr.Domain;
using Mersal.Events;

namespace Mersal.Emr.Infrastructure;

/// <summary>One step a sibling service's event asks emr to append. Pure data — no clock, no tenant, no
/// database: the consumer supplies those, so this half can be tested without either.</summary>
/// <param name="Step">One of <see cref="CareSteps"/>.</param>
/// <param name="EncounterId">The episode to attach to. Never empty — a draft without one is not produced.</param>
/// <param name="Reference">The business key of the thing the step is about (ORD-*, RX-*, AUTH-*).</param>
/// <param name="Actor">Subject id of whoever did it, when the event names a person.</param>
/// <param name="Source">Which service said so — one of <see cref="CareStepSources"/>.</param>
public sealed record CareStepDraft(string Step, Guid EncounterId, string? Reference, string? Actor, string Source);

/// <summary>
/// Translates a mirrored domain event into a care-episode step (ADR-0031).
///
/// <para><b>The rule this file exists to hold.</b> A step is a LABEL, a TIME, an ACTOR and a BUSINESS KEY.
/// Nothing here reads a test name, a drug, a dose, a result value, an ICD code or a rationale — not because
/// those fields are absent from the payloads (several are right there) but because reception and the call
/// centre read this timeline. "Medicine dispensed · RX-2026-000031" is the act; which medicine is the care,
/// and it stays behind pharmacy's own gate. The reference is the door to the thing, not the thing.</para>
///
/// <para><b>Why translation rather than storing the event name.</b> The wire says <c>OrderLinesConsumed</c>;
/// a person reading their own appointment history needs "sample taken". Persisting the publisher's vocabulary
/// would put nine services' naming choices into one patient-facing list and make every rename of theirs a
/// silent change to a clinical record.</para>
///
/// <para><b>Anything unrecognised returns null and is acked.</b> A mirrored event that maps to no step is not
/// an error — the allow-list in <see cref="CareFeed"/> and this switch are edited by different hands at
/// different times, and nacking on the difference would fill a dead-letter queue with messages that were
/// never owed an answer.</para>
/// </summary>
public static class CareEpisodeMapping
{
    /// <summary>The step this event asks for, or null when it is not one emr records, carries no encounter,
    /// or cannot be parsed. Never throws: a malformed body is simply not a step, and the consumer's ack path
    /// deals with it like any other message it cannot use.</summary>
    public static CareStepDraft? For(string? eventType, string payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(payload)) return null;

        JsonElement root;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException) { return null; }
        using (doc)
        {
            root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // The correlation key. An event without it belongs to no episode we can name, and attaching it to
            // a guessed one would put this member's order on another member's timeline — so it produces
            // nothing at all. Guid.Empty counts as absent: orders and prescriptions type the column
            // non-nullable, so "no encounter" arrives as all-zeroes rather than as null.
            var encounterId = GuidOf(root, "encounterId");
            if (encounterId is null || encounterId == Guid.Empty) return null;

            var orderNo = Str(root, "orderNo");
            var rxNo = Str(root, "rxNo");
            var authNo = Str(root, "authNo");

            return eventType switch
            {
                // ---- orders-service: the investigation leg ----
                "OrderCreated" => Draft(CareSteps.OrderPlaced, encounterId.Value, orderNo,
                    Str(root, "orderedByUserId"), CareStepSources.Orders),
                "OrderPendingApproval" => Draft(CareSteps.OrderSentForApproval, encounterId.Value, orderNo,
                    Str(root, "orderedByUserId"), CareStepSources.Orders),
                "OrderCancelled" => Draft(CareSteps.OrderCancelled, encounterId.Value, orderNo,
                    Str(root, "cancelledByUserId"), CareStepSources.Orders),
                // Consume and result carry no actor. The person who ran the test is a lab technician at a
                // performing provider, and the payload names the FACILITY, not a subject emr could resolve to
                // a name — so the step says so rather than rendering a truncated uuid as "who did this".
                "OrderLinesConsumed" => Draft(CareSteps.SampleConsumed, encounterId.Value, orderNo,
                    null, CareStepSources.Orders),
                "OrderResultUploaded" => Draft(CareSteps.ResultReported, encounterId.Value, orderNo,
                    null, CareStepSources.Orders),

                // ---- pharmacy-service: the medication leg ----
                "RxCreated" => Draft(CareSteps.PrescriptionWritten, encounterId.Value, rxNo,
                    Str(root, "orderedByUserId"), CareStepSources.Pharmacy),
                // The one CONDITIONAL mapping. Pharmacy publishes RxSubmitted for every prescription — the
                // routing outcome is the `requiresApproval` flag, not the event name — so an unconditional
                // step would put "sent for approval" on the timeline of every prescription that was never
                // sent anywhere. Read the flag, and say nothing when it is false: `RxCreated` has already
                // recorded that the prescription exists, and the auto-approval is routing mechanics.
                "RxSubmitted" => Flag(root, "requiresApproval")
                    ? Draft(CareSteps.PrescriptionSentForApproval, encounterId.Value, rxNo,
                        Str(root, "orderedByUserId"), CareStepSources.Pharmacy)
                    : null,
                "RxCancelled" => Draft(CareSteps.PrescriptionCancelled, encounterId.Value, rxNo,
                    Str(root, "cancelledByUserId"), CareStepSources.Pharmacy),
                "RxLinesDispensed" => Draft(CareSteps.MedicineDispensed, encounterId.Value, rxNo,
                    null, CareStepSources.Pharmacy),

                /*
                 * ---- approvals-service: one step for five events ----
                 *
                 * Approved, partially approved, rejected, overridden and emergency-approved all collapse to
                 * "authorization decided". The OUTCOME is a benefit decision on a named clinical request, and
                 * it is not the desk's to read off an appointment row — what the desk legitimately needs is
                 * that the wait ended and the AUTH- number to quote. Whoever is entitled to the answer opens
                 * the authorization, where approvals-service gates it.
                 *
                 * `reviewerId` is on the payload for the read model and doubles as the actor here — it is the
                 * one attribution an authorization can honestly make.
                 */
                "AuthApproved" or "AuthPartiallyApproved" or "AuthRejected"
                    or "AuthOverridden" or "AuthEmergencyApproved" =>
                    Draft(CareSteps.AuthorizationDecided, encounterId.Value, authNo,
                        Str(root, "reviewerId"), CareStepSources.Approvals),

                _ => null,
            };
        }
    }

    /// <summary>A step with no reference is still a step — it happened, and saying so with an empty key beats
    /// dropping it. A BLANK reference is normalised to null so the UI's "render the chip if there is one"
    /// does not paint an empty box.</summary>
    private static CareStepDraft Draft(string step, Guid encounterId, string? reference, string? actor, string source) =>
        new(step, encounterId, Blank(reference), Blank(actor), source);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A boolean that must be present and true. A missing or non-boolean flag reads as false, so a
    /// publisher that drops it produces no step rather than a step asserting something nobody claimed.</summary>
    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static Guid? GuidOf(JsonElement e, string name) =>
        Str(e, name) is { } s && Guid.TryParse(s, out var g) ? g : null;
}
