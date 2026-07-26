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

/// <summary>Update the call log (reason/outcome/notes).</summary>
public sealed record UpdateInteractionRequest(CallReasonCode? ReasonCode, CallOutcome? Outcome, string? Notes);

/// <summary>The interaction as returned to the agent. No identifier values — only the bound beneficiary id.</summary>
public sealed record InteractionView(
    Guid InteractionId, string CallRef, Guid? BeneficiaryId, CallDirection Direction,
    string Status, CallReasonCode? ReasonCode, CallOutcome? Outcome, string? Notes,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool Verified)
{
    public static InteractionView From(CallInteraction i, bool verified) => new(
        i.InteractionId, i.CallRef, i.BeneficiaryId, i.Direction, i.Status.ToString(),
        i.ReasonCode, i.Outcome, i.Notes, i.StartedAt, i.EndedAt, verified);
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
