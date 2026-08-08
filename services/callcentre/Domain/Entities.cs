namespace Mersal.CallCentre.Domain;

// Contact-centre domain (phase 15.1; design 37 §3 MemberScoped, 10-role-matrix Call Center, 19-audit-strategy).
// Two aggregates: a call_interaction (the call itself) and the caller_verification records bound to it.
//
// WHAT THE VERIFICATION RECORD IS NOW. Caller identity is confirmed BY THE AGENT ON THE PHONE — the platform
// records that it happened, it does not administer it. Opening a member's file writes one attestation bound to
// this interaction and this beneficiary, and that binding is what every disclose/act endpoint consults: a call
// still cannot read a member it was not opened against, and it stops disclosing the moment it closes.
//
// We never store identifier VALUES (they live in patient-service). For an off-system attestation we do not store
// identifier TYPES either — the agent does not report which identifiers they used, so recording a set would be
// inventing one.

/// <summary>Call direction. Inbound = member/clinic calls the hotline; Outbound = agent calls the member.</summary>
public enum CallDirection { Inbound, Outbound }

/// <summary>Why the call happened (drives KPI reason-mix + routing). Extend deliberately — mirrors the DB CHECK.</summary>
public enum CallReasonCode
{
    BookAppointment,
    RescheduleAppointment,
    CancelAppointment,
    AppointmentEnquiry,
    EligibilityEnquiry,
    UpdateContact,
    Complaint,
    Other,
}

/// <summary>How the call ended. Resolved counts toward first-contact-resolution; Abandoned toward the drop rate.</summary>
public enum CallOutcome { Resolved, FollowUpRequired, Transferred, Abandoned, NoAction }

/// <summary>Interaction lifecycle. Open while the agent is on the call; Closed on wrap-up (disclosure ends).</summary>
public enum InteractionStatus { Open, Closed }

/// <summary>The result of a caller-verification record.</summary>
public enum VerificationResult { Passed, Failed }

/// <summary>WHERE the caller's identity was confirmed.
///
/// <para><see cref="OnSystem"/> is the historical phase-15 challenge: the agent ticked ≥2 identifier types on
/// screen and the platform decided whether that was enough. <see cref="OffSystem"/> is the current operation —
/// the agent confirms identity on the phone and the platform records their attestation.</para>
///
/// <para>The distinction is kept rather than collapsed BECAUSE OF THE ROWS ALREADY IN THE TABLE. Reading an old
/// challenge as an attestation, or an attestation as a challenge, would misreport what the platform actually did
/// on a given call — and this table is audit evidence, not a cache.</para></summary>
public enum VerificationMethod { OnSystem, OffSystem }

/// <summary>The call itself. <see cref="BeneficiaryId"/> is null until a member is identified; a PASS binds the
/// interaction to that beneficiary and is the anchor the verification gate consults. Never hard-deleted.</summary>
public sealed class CallInteraction
{
    public Guid InteractionId { get; set; }
    public string CallRef { get; set; } = default!;                // CALL-YYYY-NNNNNN
    public string TenantId { get; set; } = default!;
    public Guid? BeneficiaryId { get; set; }                       // null until identified + verified
    public Guid AgentUserId { get; set; }
    public CallDirection Direction { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public CallReasonCode? ReasonCode { get; set; }
    public CallOutcome? Outcome { get; set; }

    /// <summary>The AGENT'S working text. Phase 20 deliberately did NOT promote this to other roles — see
    /// <see cref="Summary"/>.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// The operational account of the call, written at wrap-up and read by OTHER roles through the patient
    /// profile (design 39 §5b). Required at close unless the outcome is Abandoned; capped at 500 characters.
    ///
    /// <para>Separate from <see cref="Notes"/> on purpose, and the separation is the feature: widening the
    /// audience for call history must not silently widen the audience for whatever an agent typed mid-call.</para>
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>Set on the first correction. Drives the visible "edited" marker — a summary other roles rely on
    /// that can be rewritten without trace is worse than no summary, because it still reads as a record.</summary>
    public DateTimeOffset? SummaryEditedAt { get; set; }
    public string? SummaryEditedBy { get; set; }

    public InteractionStatus Status { get; set; } = InteractionStatus.Open;
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint RowVersion { get; set; }                           // xmin — optimistic concurrency

    public List<CallerVerification> Verifications { get; set; } = [];
}

/// <summary>A caller-verification record: WHO the agent says they are speaking to, on WHICH call, and WHERE that
/// was confirmed. It is the anchor the disclosure gate consults.
///
/// <para><see cref="VerifiedIdentifierTypes"/> records only WHICH identifier TYPES were confirmed (e.g.
/// ["MemberNo","DateOfBirth"]) — NEVER the values, which live in patient-service. It is populated only for
/// <see cref="VerificationMethod.OnSystem"/> rows; an off-system attestation leaves it empty because the agent
/// does not report which identifiers they asked for.</para></summary>
public sealed class CallerVerification
{
    public Guid VerificationId { get; set; }
    public Guid InteractionId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public string TenantId { get; set; } = default!;
    /// <summary>The identifier TYPES confirmed — stored as a JSON string array. Values are NEVER stored here.
    /// Empty for off-system attestations.</summary>
    public List<string> VerifiedIdentifierTypes { get; set; } = [];
    public VerificationResult Result { get; set; }

    /// <summary>Where identity was confirmed. Defaults to <see cref="VerificationMethod.OnSystem"/> so the rows
    /// written before this column existed keep meaning what they meant.</summary>
    public VerificationMethod Method { get; set; } = VerificationMethod.OnSystem;

    public string? FailureReason { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public string? VerifiedBy { get; set; }
}

/// <summary>An append-only record of a summary correction (design 39 §5b: edits keep history and carry a
/// visible "edited" marker, never a silent overwrite).</summary>
public sealed class CallSummaryRevision
{
    public Guid RevisionId { get; set; }
    public Guid InteractionId { get; set; }
    public string TenantId { get; set; } = default!;
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string? EditedBy { get; set; }
    public DateTimeOffset EditedAt { get; set; }
}

/// <summary>What is left of the phase-15 verification rules once identity is confirmed off-system.
///
/// <para><b>Deliberately almost empty.</b> The minimum-identifier-types threshold, the challengeable-type
/// allow-list, the failed-attempt cap and the pre-verification masking helper are all GONE, because each was a
/// property of an ON-SCREEN CHALLENGE the platform no longer administers. A minimum on a set nobody submits, a
/// cap on attempts that cannot fail, and a mask on a number the agent is now shown in full are not controls —
/// they are the shape of a control, and leaving them behind would let this file keep claiming a guarantee the
/// system stopped providing.</para>
///
/// <para>The rule that survives is not here at all: it is the interaction binding in
/// <c>VerificationService.IsVerifiedAsync</c> — a call may only disclose the member it was opened against, and
/// only while it is open.</para></summary>
public static class VerificationPolicy
{
    /// <summary>The identifier TYPES that may be recorded on an ON-SYSTEM verification. Retained as an allow-list
    /// for reading historical rows and for any future re-introduction of a challenge; nothing writes through it
    /// today. Values are never stored — only type names.</summary>
    public static readonly IReadOnlySet<string> ChallengeableTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "MemberNo", "NationalId", "Passport", "RefugeeId", "UnhcrNo", "DateOfBirth", "Phone",
    };
}

/// <summary>Business-key formatter (0A §3). CALL-YYYY-NNNNNN — 6-digit zero-padded per-year sequence, consistent
/// with ORD-/RX-/AUTH-/CASE- keys.</summary>
public static class CallRef
{
    public static string Format(int year, int sequence) => $"CALL-{year:D4}-{sequence:D6}";
}
