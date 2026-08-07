using Mersal.Pharmacy.Domain;

namespace Mersal.Pharmacy.Api;

/// <summary>Create + submit an e-prescription (US-033). Created Draft then transitioned to Submitted; advisory
/// interaction/allergy alerts are surfaced. <see cref="AcknowledgeAlerts"/> records a prescriber override.</summary>
/// <param name="DiagnosisIcdCodes">
/// 26.4 — the encounter's recorded diagnoses, snapshotted onto the prescription. The indication check has
/// nothing to compare against without them, and an empty list is reported as "no diagnosis recorded" rather
/// than passed.
/// </param>
/// <param name="Acknowledgements">
/// 26.4 — a reason per warning the prescriber is proceeding past. The ACKNOWLEDGEMENT gates submission, not
/// the warning: an unacknowledged warning is a 422, and an acknowledged one is recorded and allowed.
/// </param>
/// <param name="Kind">30.x — "Acute" (the default and the behaviour every existing caller gets) or "Chronic".
/// A chronic script carries a refill cadence and a duration greater than one month, and its lines are issued
/// as dated refill WINDOWS rather than as one collection (design 45 §5).</param>
/// <param name="RefillFrequencyCode">The supervisor-configurable cadence (<c>pharmacy.refill_frequency</c>).
/// Required on a chronic script, refused on an acute one — "is this chronic?" has exactly one answer.</param>
/// <param name="DurationDays">The script-level treatment length. Chronic requires &gt; 30: a 14-day course is
/// not chronic.</param>
public sealed record CreatePrescriptionRequest(
    Guid BeneficiaryId, Guid EncounterId, DateTimeOffset? ExpiresAt, bool AcknowledgeAlerts, List<CreateRxLine> Lines,
    List<string>? DiagnosisIcdCodes = null,
    List<LineAcknowledgement>? Acknowledgements = null,
    string? Kind = null,
    string? RefillFrequencyCode = null,
    int? DurationDays = null);

/// <param name="DurationDays">26.4 — treatment length; what makes a dose ceiling or duration limit checkable.</param>
/// <param name="ClientLineId">
/// Client-side line identity, so findings and acknowledgements can refer to a line before the server has
/// given it one. Regenerated server-side on submit; never trusted as a database key.
/// </param>
/// <param name="DoseAmount">Numeric dose, when the client can supply one, for the daily-dose rule.</param>
/// <param name="TimesPerDay">Doses per day, for the daily-dose rule.</param>
public sealed record CreateRxLine(
    Guid DrugId, string? Dose, string? Route, string? Frequency, decimal QuantityPrescribed, int RefillsAllowed,
    int? DurationDays = null,
    Guid? ClientLineId = null,
    decimal? DoseAmount = null,
    string? DoseUnit = null,
    int? TimesPerDay = null);

/// <summary>A prescriber's recorded reason for proceeding past one warning on one line.</summary>
public sealed record LineAcknowledgement(Guid ClientLineId, string FindingKind, string Reason);

/// <summary>
/// Body for POST /prescriptions/validate — step 1, advisory (doc 43 §5).
/// </summary>
/// <remarks>
/// Nothing is persisted as a draft prescription by this call; only the validation run itself is recorded.
/// Its verdict is <b>never</b> an input to submission: step 2 re-evaluates from scratch server-side.
/// </remarks>
public sealed record ValidatePrescriptionRequest(
    Guid BeneficiaryId, Guid EncounterId, List<CreateRxLine> Lines, List<string>? DiagnosisIcdCodes = null);

/// <summary>One finding, as the workspace renders it.</summary>
/// <param name="State">Ok | Warning | Blocked | NotChecked | Unavailable — five, never four.</param>
public sealed record FindingView(
    Guid LineId, Guid? DrugId, string Kind, string State,
    string MessageEn, string MessageAr,
    string? SourceName, string? SourceVersion, DateTimeOffset? CheckedAt, string? Caveat,
    string? Severity, Guid? RelatedLineId,
    bool RequiresAcknowledgement, bool IsBlocking,
    // Quoted source text — a label sentence naming another drug, or the labelled dosing. English only,
    // because that is the language the label is published in.
    string? ReferenceText = null);

/// <summary>The result of a validation run, per line and overall.</summary>
public sealed record ValidationResultView(
    Guid ValidationId, DateTimeOffset RanAt, string EngineVersion, string OverallState,
    IReadOnlyList<FindingView> Findings,
    IReadOnlyDictionary<Guid, string> LineStates);

public sealed record CreateReferralRequest(
    Guid BeneficiaryId, Guid EncounterId, string TargetSpecialty, Guid? TargetProviderId, string? Reason);

public sealed record CancelRequest(string? Reason);

public sealed record RxLineResponse(
    Guid PrescriptionLineId, Guid DrugId, string? DrugName, string? Dose, string? Route, string? Frequency,
    decimal QuantityPrescribed, decimal QuantityDispensed, int RefillsAllowed, string Status)
{
    public static RxLineResponse From(PrescriptionLine l) => new(
        l.PrescriptionLineId, l.DrugId, l.DrugName, l.Dose, l.Route, l.Frequency,
        l.QuantityPrescribed, l.QuantityDispensed, l.RefillsAllowed, l.Status.ToString());
}

public sealed record AlertView(string Kind, string Severity, string Detail);

public sealed record PrescriptionResponse(
    Guid PrescriptionId, string RxNo, Guid BeneficiaryId, Guid EncounterId, Guid PrescriberId,
    string? PrescriberName,
    string Status, bool Dispensable, DateTimeOffset? SubmittedAt, DateTimeOffset? ExpiresAt,
    IReadOnlyList<RxLineResponse> Lines, IReadOnlyList<AlertView> Alerts,
    /// <summary>The approvals authorization behind this prescription's <c>Approved</c> status, if there is one.
    ///
    /// <para><b>Why a reader needs it.</b> <c>Approved</c> is reached two ways (doc 23 §3): the approval team
    /// decides it, or the routing policy found no gate and the submit path set it outright
    /// (<c>if (!route.RequiresApproval) rx.Status = RxStatus.Approved</c>). The status string cannot tell
    /// those apart, so every screen rendering it as "Approved" was telling a prescriber that a reviewer had
    /// passed their prescription when, for an ungated one, nobody had looked at it.</para>
    ///
    /// <para>Null therefore means auto-cleared, and clients label it as verified rather than approved.</para></summary>
    Guid? AuthorizationId = null)
{
    public static PrescriptionResponse From(Prescription p, IReadOnlyList<AlertView>? alerts = null) => new(
        p.PrescriptionId, p.RxNo, p.BeneficiaryId, p.EncounterId, p.PrescriberId, p.PrescriberName,
        p.Status.ToString(),
        PrescriptionWorkflow.IsDispensable(p.Status), p.SubmittedAt, p.ExpiresAt,
        p.Lines.Select(RxLineResponse.From).ToList(), alerts ?? [], p.AuthorizationId);
}

// ---- Phase 6 dispensing (min-necessary: drug/dose/route/frequency + remaining qty + patient id ONLY; never
// diagnoses/notes/investigation results) ----

/// <summary>A dispensable line for the pharmacist queue — only what is needed to dispense. Fully-dispensed/cancelled
/// lines are omitted by the projection.</summary>
/// <param name="DurationDays">
/// How long the course runs. NULL means the prescriber did not record one — said in those words at the
/// counter rather than left blank, because a missing duration and a one-day course look identical in an
/// empty cell, and only one of them is a reason to ring the prescriber.
/// </param>
public sealed record DispensableLineView(
    Guid PrescriptionLineId, Guid DrugId, string? DrugName, string? Dose, string? Route, string? Frequency,
    int? DurationDays,
    decimal QuantityPrescribed, decimal QuantityDispensed, decimal QuantityRemaining, string Status);

/// <summary>
/// A prescription as the dispensing counter needs it.
///
/// <para><see cref="PrescriberName"/> and the lines' drug names are why this view is readable at all. It
/// used to carry ids only, so the screen rendered the literal words "Prescriber" and "Medication" beside a
/// raw uuid — a pharmacist cannot check a prescription against the packet in their hand from that.</para>
/// </summary>
/// <param name="PrimaryIcdCode">The encounter's primary diagnosis at prescribing time.</param>
/// <param name="DiagnosisCodes">
/// Every ICD code recorded on the encounter when the prescription was written — a SNAPSHOT, not a join.
/// <para>The counter needs it because a medicine only makes sense against what it is FOR: a pharmacist
/// checking a broad-spectrum antibiotic against "acute sinusitis" is doing something different from one
/// handing it over blind. It is the same snapshot the indication check ran on (26.4), so the screen and the
/// warning cannot disagree about what was known at the time.</para>
/// </param>
public sealed record DispensableRxView(
    Guid PrescriptionId, string RxNo, Guid BeneficiaryId, string Status, DateTimeOffset? ExpiresAt,
    string? PrescriberName, DateTimeOffset? SubmittedAt,
    string? PrimaryIcdCode, IReadOnlyList<string> DiagnosisCodes,
    /// <summary>
    /// Past its validity window, computed against the clock rather than read from <c>Status</c>.
    ///
    /// <para>The sweeper moves the STATUS to Expired on a timer, so between the moment a prescription lapses
    /// and the next sweep the row still says Approved. A counter that trusted the status would hand over
    /// medication in that gap. The dispense rule has always compared the date; this makes the SCREEN agree
    /// with it, instead of showing "Approved" on something the server will refuse.</para>
    /// </summary>
    bool Expired,
    IReadOnlyList<DispensableLineView> Lines)
{
    public static DispensableRxView From(Prescription p, DateTimeOffset now) => new(
        p.PrescriptionId, p.RxNo, p.BeneficiaryId, p.Status.ToString(), p.ExpiresAt,
        p.PrescriberName, p.SubmittedAt,
        p.PrimaryIcdCode, DiagnosisCodesJson.Parse(p.DiagnosisSnapshot),
        p.Status == RxStatus.Expired || (p.ExpiresAt is { } exp && exp <= now),
        p.Lines.Where(l => l.Status is RxLineStatus.Active or RxLineStatus.PartiallyDispensed && l.QuantityRemaining > 0)
            .Select(l => new DispensableLineView(
                l.PrescriptionLineId, l.DrugId, l.DrugName, l.Dose, l.Route, l.Frequency, l.DurationDays,
                l.QuantityPrescribed, l.QuantityDispensed, l.QuantityRemaining, l.Status.ToString()))
            .ToList());
}

/// <summary>Reads the diagnosis snapshot back out. A malformed value yields an empty list rather than
/// throwing — a counter that 500s because one row's json is odd is worse than one that shows fewer facts.</summary>
internal static class DiagnosisCodesJson
{
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }
}

/// <summary>Dispense a quantity of a line against a batch/lot with its expiry. <see cref="SubstitutedDrugId"/> +
/// <see cref="SubstitutionReason"/> record a policy-approved substitution (6.3); omit them for a straight dispense.</summary>
/// <param name="Note">Optional — what the pharmacist recorded about this handover. Not clinical; see
/// <c>DispenseEvent.Note</c>.</param>
public sealed record DispenseRequest(
    decimal Quantity, string BatchNo, DateOnly ExpiryDate, Guid? SubstitutedDrugId, string? SubstitutionReason,
    string? Note = null);

public sealed record DispenseEventView(
    Guid DispenseId, Guid PrescriptionLineId, decimal Quantity, string BatchNo, DateOnly ExpiryDate,
    Guid? SubstitutedDrugId, string? SubstitutionReason, string? Note, DateTimeOffset DispensedAt)
{
    public static DispenseEventView From(DispenseEvent d) => new(
        d.DispenseId, d.PrescriptionLineId, d.Quantity, d.BatchNo, d.ExpiryDate,
        d.SubstitutedDrugId, d.SubstitutionReason, d.Note, d.DispensedAt);
}

public sealed record DispenseResponse(string RxStatus, bool Replayed, DispenseEventView Dispense, DispensableRxView Prescription)
{
    public static DispenseResponse From(Prescription rx, DispenseEvent evt, bool replayed, DateTimeOffset now) => new(
        rx.Status.ToString(), replayed, DispenseEventView.From(evt), DispensableRxView.From(rx, now));
}

public sealed record OutOfStockRequest(Guid PrescriptionLineId, decimal? Quantity, string? Note);

public sealed record ReferralResponse(
    Guid ReferralId, string ReferralNo, Guid BeneficiaryId, Guid EncounterId, Guid ReferringProviderId,
    string TargetSpecialty, Guid? TargetProviderId, string? Reason, string Status, DateTimeOffset RequestedAt)
{
    public static ReferralResponse From(Referral r) => new(
        r.ReferralId, r.ReferralNo, r.BeneficiaryId, r.EncounterId, r.ReferringProviderId,
        r.TargetSpecialty, r.TargetProviderId, r.Reason, r.Status.ToString(), r.RequestedAt);
}
