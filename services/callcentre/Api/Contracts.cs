using Mersal.CallCentre.Domain;

namespace Mersal.CallCentre.Api;

// Request/response DTOs for the call-centre API (phase 15). Records; min-necessary fields only. No identifier
// VALUES cross this boundary in either direction — the caller states them verbally, to the agent, off-system.

/// <summary>Open a new call interaction.</summary>
public sealed record OpenInteractionRequest(CallDirection Direction, CallReasonCode? ReasonCode);

/// <summary>Attest that the agent has confirmed, on the call, who they are speaking to — and bind the interaction
/// to that member.
///
/// <para><b>One field, and that is the whole contract.</b> It used to carry the identifier types the agent ticked,
/// the pass/fail result and a failure reason, because the platform decided whether the challenge was good enough.
/// It no longer runs that decision, so accepting those fields would invite a client to send a set of identifier
/// types that nothing checks and nothing means.</para></summary>
public sealed record RecordVerificationRequest(Guid BeneficiaryId);

/// <summary>Update the call log.
///
/// <para><b><paramref name="Summary"/> is the only writable text on a call.</b> There was a second field —
/// <c>Notes</c>, the agent's private working text, kept apart so that widening the audience for call history
/// would not silently widen the audience for whatever was typed mid-call. The call centre now writes one account
/// of the call, which is the operational one other roles read on the patient profile, so the distinction had
/// nothing left to protect. The column survives and old notes stay readable; nothing writes to it.</para></summary>
public sealed record UpdateInteractionRequest(
    CallReasonCode? ReasonCode, CallOutcome? Outcome, string? Summary = null);

/// <summary>Correct a summary after the fact. Kept separate from <see cref="UpdateInteractionRequest"/> because
/// it is the one field editable after close, and it writes a revision every time.</summary>
public sealed record UpdateSummaryRequest(string? Summary);

/// <summary>The interaction as returned to the agent. No identifier values — only the bound beneficiary id.</summary>
public sealed record InteractionView(
    Guid InteractionId, string CallRef, Guid? BeneficiaryId, CallDirection Direction,
    string Status, CallReasonCode? ReasonCode, CallOutcome? Outcome, string? Notes,
    string? Summary, bool SummaryEdited,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool Verified)
{
    public static InteractionView From(CallInteraction i, bool verified) => new(
        i.InteractionId, i.CallRef, i.BeneficiaryId, i.Direction, i.Status.ToString(),
        i.ReasonCode, i.Outcome, i.Notes, i.Summary, i.SummaryEditedAt is not null,
        i.StartedAt, i.EndedAt, verified);
}

/// <summary>A verification record as returned. <paramref name="Method"/> says where identity was confirmed, so a
/// reader can tell an off-system attestation from a historical on-screen challenge without guessing from an
/// empty type list.</summary>
public sealed record VerificationView(
    Guid VerificationId, Guid BeneficiaryId, IReadOnlyList<string> VerifiedIdentifierTypes,
    string Result, string Method, string? FailureReason, DateTimeOffset VerifiedAt)
{
    public static VerificationView From(CallerVerification v) => new(
        v.VerificationId, v.BeneficiaryId, v.VerifiedIdentifierTypes, v.Result.ToString(),
        v.Method.ToString(), v.FailureReason, v.VerifiedAt);
}

/// <summary>A page of interactions.</summary>
public sealed record InteractionListResponse(IReadOnlyList<InteractionView> Items, string? NextCursor);

// --- 15.3 appointment actions from the call ---------------------------------------------------------------

/// <summary>Book an appointment from the call. Delegates to emr POST /appointments; the interaction must be
/// verified for <paramref name="BeneficiaryId"/>. <paramref name="ReferralRef"/>/<paramref name="OriginEncounterId"/>
/// convert a referral/follow-up in one step (15.4).</summary>
public sealed record BookFromCallRequest(
    Guid InteractionId, Guid BeneficiaryId, Guid SlotId, string AppointmentType,
    Guid? BranchId, string? ReferralRef, Guid? OriginEncounterId,
    // 14.5 — the agent picks a DOCTOR (not just a clinic) and may record a general/administrative note.
    // Both are forwarded verbatim; emr owns their validation, exactly as it owns the no-double-book rule.
    Guid? DoctorId = null, string? Note = null);

/// <summary>Reschedule an appointment from the call (delegates to emr; carries If-Match from the prior read).</summary>
public sealed record RescheduleFromCallRequest(Guid InteractionId, Guid NewSlotId);

/// <summary>Cancel an appointment from the call. <paramref name="ReasonCode"/> is MANDATORY (a cancel without one
/// is 422).</summary>
public sealed record CancelFromCallRequest(Guid InteractionId, CallCancelReason? ReasonCode, string? Note);

// --- 15.4 contact edits from the call ---------------------------------------------------------------------

/// <summary>Correct an existing contact (delegates to patient-service, which keeps history).</summary>
public sealed record UpdateContactFromCallRequest(Guid InteractionId, string Kind, string Value, string? PreferredChannel);

/// <summary>Add a new contact (delegates to patient-service; may mark primary — patient owns the one-primary rule).</summary>
public sealed record AddContactFromCallRequest(Guid InteractionId, string Kind, string Value, bool IsPrimary, string? PreferredChannel);
