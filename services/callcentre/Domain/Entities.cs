namespace Mersal.CallCentre.Domain;

// Contact-centre domain (phase 15.1; design 37 §3 MemberScoped, 10-role-matrix Call Center, 19-audit-strategy).
// Two aggregates: a call_interaction (the call itself) and the caller_verification attempts bound to it.
//
// THE DEFINING PRIVACY CONTROL: nothing about a member is disclosed until the agent records a SUCCESSFUL
// verification for THIS interaction and THIS beneficiary. And we never store the identifier VALUES the caller
// recited — only WHICH identifier TYPES were confirmed (the values live in patient-service).

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

/// <summary>Interaction lifecycle. Open while the agent is on the call; Closed on wrap-up (verification expires).</summary>
public enum InteractionStatus { Open, Closed }

/// <summary>The result of a caller-verification attempt.</summary>
public enum VerificationResult { Passed, Failed }

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

/// <summary>A caller-verification attempt. <see cref="VerifiedIdentifierTypes"/> records only WHICH identifier
/// TYPES were confirmed verbally (e.g. ["MemberNo","DateOfBirth"]) — NEVER the values. A Fail is persisted and
/// audited (never silently dropped). A Pass requires ≥ the configured minimum distinct types.</summary>
public sealed class CallerVerification
{
    public Guid VerificationId { get; set; }
    public Guid InteractionId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public string TenantId { get; set; } = default!;
    /// <summary>The identifier TYPES confirmed — stored as a JSON string array. Values are NEVER stored here.</summary>
    public List<string> VerifiedIdentifierTypes { get; set; } = [];
    public VerificationResult Result { get; set; }
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

/// <summary>Pure verification rules (no I/O) so the gate + tests share one source of truth (design 37, US privacy).
/// The Call Centre must confirm at least <see cref="MinIdentifierTypes"/> DISTINCT identifier types for a Pass.</summary>
public static class VerificationPolicy
{
    /// <summary>Default minimum distinct identifier types for a Pass (configurable; 2 per the prompt).</summary>
    public const int MinIdentifierTypes = 2;

    /// <summary>The identifier TYPES an agent may challenge on. Values live in patient-service; only the type name
    /// is ever recorded here. Kept as an allow-list so a caller can't smuggle a value in as a "type".</summary>
    public static readonly IReadOnlySet<string> ChallengeableTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "MemberNo", "NationalId", "Passport", "RefugeeId", "UnhcrNo", "DateOfBirth", "Phone", "FullName",
    };

    /// <summary>Distinct, known types only. Anything outside the allow-list is ignored (defensive against values
    /// being passed as types).</summary>
    public static IReadOnlyList<string> Normalise(IEnumerable<string>? types) =>
        (types ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t) && ChallengeableTypes.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Whether the confirmed set is sufficient for a Pass.</summary>
    public static bool MeetsThreshold(IReadOnlyList<string> normalisedTypes, int min = MinIdentifierTypes) =>
        normalisedTypes.Count >= min;
}

/// <summary>Business-key formatter (0A §3). CALL-YYYY-NNNNNN — 6-digit zero-padded per-year sequence, consistent
/// with ORD-/RX-/AUTH-/CASE- keys.</summary>
public static class CallRef
{
    public static string Format(int year, int sequence) => $"CALL-{year:D4}-{sequence:D6}";
}
