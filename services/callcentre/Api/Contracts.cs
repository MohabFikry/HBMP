using Mersal.CallCentre.Domain;

namespace Mersal.CallCentre.Api;

// Request/response DTOs for the call-centre API (phase 15). Records; min-necessary fields only. Note the
// verification request carries identifier TYPE names ONLY — never values (the caller states the value verbally).

/// <summary>Open a new call interaction.</summary>
public sealed record OpenInteractionRequest(CallDirection Direction, CallReasonCode? ReasonCode);

/// <summary>Record a caller-verification attempt. <paramref name="VerifiedIdentifierTypes"/> is a list of identifier
/// TYPE names the agent confirmed verbally (e.g. ["MemberNo","DateOfBirth"]) — NEVER the values.</summary>
public sealed record RecordVerificationRequest(
    Guid BeneficiaryId,
    List<string> VerifiedIdentifierTypes,
    VerificationResult Result,
    string? FailureReason);

/// <summary>Update the call log. <paramref name="Summary"/> (phase 20.3b) is the operational account OTHER roles
/// read; <paramref name="Notes"/> stays the agent's own working text and is never promoted.</summary>
public sealed record UpdateInteractionRequest(
    CallReasonCode? ReasonCode, CallOutcome? Outcome, string? Notes, string? Summary = null);

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

/// <summary>A verification attempt as returned (types only, never values).</summary>
public sealed record VerificationView(
    Guid VerificationId, Guid BeneficiaryId, IReadOnlyList<string> VerifiedIdentifierTypes,
    string Result, string? FailureReason, DateTimeOffset VerifiedAt)
{
    public static VerificationView From(CallerVerification v) => new(
        v.VerificationId, v.BeneficiaryId, v.VerifiedIdentifierTypes, v.Result.ToString(),
        v.FailureReason, v.VerifiedAt);
}

/// <summary>A page of interactions.</summary>
public sealed record InteractionListResponse(IReadOnlyList<InteractionView> Items, string? NextCursor);

// --- 15.3 appointment actions from the call ---------------------------------------------------------------

/// <summary>Book an appointment from the call. Delegates to emr POST /appointments; the interaction must be
/// verified for <paramref name="BeneficiaryId"/>. <paramref name="ReferralRef"/>/<paramref name="OriginEncounterId"/>
/// convert a referral/follow-up in one step (15.4).</summary>
public sealed record BookFromCallRequest(
    Guid InteractionId, Guid BeneficiaryId, Guid SlotId, string AppointmentType,
    Guid? BranchId, string? ReferralRef, Guid? OriginEncounterId);

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
