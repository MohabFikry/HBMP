namespace Mersal.Pharmacy.Api;

/// <summary>
/// 31.6 — the response shapes this service returns, written down.
///
/// ============================================================================================================
/// WHY THESE EXIST
/// ============================================================================================================
/// An anonymous object returned from an endpoint IS a contract — the SPA parses it with a zod schema, and a
/// property renamed on this side surfaces over there as "could not load", at a dispensing counter, with
/// nothing failing in between. It was simply an UNWRITTEN contract: a minimal API returning
/// <c>Results.Ok(new { … })</c> publishes no schema, so the OpenAPI drift gate compared the route and the
/// request and passed silently over the body.
///
/// Naming them changes no payload. Each record below carries exactly the property names the anonymous object
/// carried, in the same casing, so the JSON is byte-identical; what changes is that the shape now appears in
/// <c>docs/api/pharmacy.json</c> and a change to it shows up as drift.
/// </summary>
/// <remarks>
/// Records, not classes, and positional rather than initialised: a positional record cannot be constructed
/// with a property missing, which is the one way a response could silently lose a field.
/// </remarks>

// ---------------------------------------------------------------------------------- reference data

/// <summary>A coded reason an amendment or withdrawal may cite, in both languages.</summary>
/// <remarks>
/// Bilingual because the picker renders in the user's language and the CODE is what is stored: a reason
/// chosen in Arabic and audited in English must be the same fact, which it can only be if the code travels.
/// </remarks>
public sealed record AmendmentReasonView(string Code, string NameEn, string NameAr);

/// <summary>A refill cadence a chronic prescription may be written against.</summary>
/// <param name="Months">The window length. It is the number the schedule is computed from, not a label.</param>
public sealed record RefillFrequencyView(string Code, int Months, string NameEn, string NameAr);

// ---------------------------------------------------------------------------------- the two previews

/// <summary>One collection window of a chronic script.</summary>
/// <param name="ScheduledOpen">The date the window is due, ISO yyyy-MM-dd.</param>
/// <param name="OpensAt">When it may actually be collected — earlier than due by the tolerance.</param>
/// <param name="ClosesAt">After this the window is forfeited, which is why it is shown before submitting.</param>
public sealed record ChronicWindowView(
    int WindowNo, string ScheduledOpen, string OpensAt, string ClosesAt, decimal AllocatedQuantity);

/// <summary>What a chronic script will actually schedule, shown before the doctor commits.</summary>
/// <param name="Unit">What <paramref name="Total"/> counts — prescribing units or whole packs.</param>
public sealed record ChronicPreviewView(
    decimal Total, string Unit, int FrequencyMonths, IReadOnlyList<ChronicWindowView> Windows);

/// <summary>
/// How much will be dispensed, computed by <c>QuantityMath</c> before the doctor commits.
/// </summary>
/// <param name="Boxes">
/// 31.3 — NULL where the catalogue does not record what a box holds. The composer shows the dose total and
/// says so, rather than printing a box count derived from a pack size that counts containers.
/// </param>
/// <param name="PackContent">How many prescribing units one box holds — the divisor, not the pack size.</param>
public sealed record QuantityPreviewView(
    decimal TotalUnits,
    decimal DispenseQuantity,
    decimal? Packs,
    decimal? Boxes,
    decimal? PackContent,
    string? PrescribingUnit,
    bool? IsPackSplittable);

// ---------------------------------------------------------------------------------- amendment outcomes

/// <summary>The result of amending one line: what was superseded, and by what.</summary>
/// <param name="Replayed">
/// True when the idempotency key had already been applied. The caller is told rather than shown a second
/// success — "it worked" and "it had already worked" are different answers to a retry.
/// </param>
public sealed record AmendLineResultView(
    Guid RxId, Guid PrescriptionLineId, Guid AmendmentId, Guid NewLineId, bool Replayed);

/// <summary>An amendment that also moved the authorisation, which the prescriber has to be told about.</summary>
/// <param name="ReturnedForApproval">
/// The amendment took the line beyond what was approved, so it is back with the approvals team. Stated as its
/// own field rather than inferred from <paramref name="AuthorizationImpact"/>, because the caller acts on it.
/// </param>
public sealed record AmendLineImpactView(
    Guid RxId, Guid PrescriptionLineId, Guid AmendmentId, Guid NewLineId, bool Replayed,
    string AuthorizationImpact, bool ReturnedForApproval);

/// <summary>A per-line report from a bulk withdrawal — named, never counted.</summary>
/// <remarks>
/// "3 of 5 withdrawn" is a true sentence that tells a prescriber nothing about WHICH two are still going to
/// be dispensed. The refusals travel with their reasons.
/// </remarks>
public sealed record CancelLinesResultView(Guid RxId, int Cancelled, IReadOnlyList<object> Lines);

/// <summary>A prescription's validity window after an extension.</summary>
public sealed record ExtendValidityView(
    Guid PrescriptionId, string RxNo, DateTimeOffset? ExpiresAt, bool Replayed);

// ---------------------------------------------------------------------------------- history

/// <summary>
/// The prescription half of a patient's service history, wrapped in its envelope.
/// </summary>
/// <remarks>
/// An envelope rather than a bare array, and it stays one: the modal distinguishes "no previous occurrences"
/// from "this half could not be loaded", and a bare array has nowhere to say which it is.
/// </remarks>
public sealed record RxHistoryView(IReadOnlyList<object> Items);

/// <summary>Liveness. Not a health CHECK — it answers only that the process is up.</summary>
public sealed record LiveView(string Status, string Service);
