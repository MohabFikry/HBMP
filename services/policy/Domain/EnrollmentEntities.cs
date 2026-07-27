namespace Mersal.Policy.Domain;

// Phase 19.2 + 19.2b — policy_plan, member_group, enrollment, enrollment_event (design 38 §3–§4.2).
//
// WINDOW SEMANTICS. Everything in this file uses an INCLUSIVE window [from, to]: a termination effective
// 31 December means the member IS covered on 31 December. plan_version (19.1) uses a HALF-OPEN window because
// a configuration boundary is naturally "the first day the new rules apply", whereas a membership boundary is
// naturally "the last day of cover" — and the shipped EligibilityEngine already reads coverage inclusively.
// The two conventions are each correct in their own domain; the danger is only in mixing them silently.

public enum PolicyPlanStatus { Active, Closed }
public enum MemberGroupType { Programme, Cohort, BranchCaseload, Campaign }
public enum MemberGroupStatus { Active, Closed }
public enum Relationship { Principal, Spouse, Child, Dependent }
public enum EnrollmentStatus { Pending, Active, Suspended, Terminated, Cancelled }
public enum EnrollmentEventType { Enrolled, GroupChanged, PlanChanged, Suspended, Reinstated, Terminated, Corrected }

/// <summary>
/// One of the plans a policy offers (19.2b). A policy is a contract with a payer; the plans under it are the
/// benefit packages a member can be elected onto — "Standard", "Oncology", "Staff" — each pointing at an
/// effective-dated plan version.
/// </summary>
public sealed class PolicyPlan
{
    public Guid PolicyPlanId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PolicyId { get; set; }
    public Guid PlanVersionId { get; set; }
    public string PlanLabel { get; set; } = default!;
    public DateOnly EffectiveFrom { get; set; }
    /// <summary>INCLUSIVE last day; null = open-ended.</summary>
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>The plan a member lands on when none is named. At most one per policy (partial unique index):
    /// two would make that resolution a coin toss over what someone is entitled to.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Declarative election criteria as jsonb — see <see cref="PlanEligibility"/>. Kept as data so a
    /// restriction is something an administrator can read and change, not a branch in code they cannot see.</summary>
    public string? EligibilityRule { get; set; }

    public int? MaxMembers { get; set; }
    public PolicyPlanStatus Status { get; set; } = PolicyPlanStatus.Active;
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Inclusive containment — the end day is still covered.</summary>
    public bool Covers(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo.Value);
}

/// <summary>A cohort inside a policy: a programme intake, a branch caseload, a campaign.</summary>
public sealed class MemberGroup
{
    public Guid GroupId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PolicyId { get; set; }
    public string GroupCode { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public MemberGroupType GroupType { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public MemberGroupStatus Status { get; set; } = MemberGroupStatus.Active;
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// The membership record. Its window is what generates the member's coverage, so the dates here are the dates
/// a receptionist's eligibility check ultimately reads.
/// </summary>
public sealed class Enrollment
{
    public Guid EnrollmentId { get; set; }
    public string TenantId { get; set; } = "";
    /// <summary>Logical FK to patient-service — a value, never a cross-schema constraint.</summary>
    public Guid BeneficiaryId { get; set; }
    public Guid PolicyId { get; set; }
    /// <summary>19.2b: always set. There is no "enrolled but on no plan".</summary>
    public Guid PolicyPlanId { get; set; }
    public Guid? GroupId { get; set; }
    public string MemberNo { get; set; } = default!;
    public Relationship Relationship { get; set; } = Relationship.Principal;
    public Guid? PrincipalEnrollmentId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    /// <summary>INCLUSIVE last day of cover; null = open-ended.</summary>
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>The LAST day still inside the waiting period. A service on this date is not yet payable; the
    /// day after is. Null = no waiting period applied.</summary>
    public DateOnly? WaitingPeriodEndsOn { get; set; }

    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Pending;
    public string? TerminationReason { get; set; }

    /// <summary>Which plan version this member's coverage was generated from. The provenance that makes an
    /// entitlement explainable back to a dated, immutable configuration.</summary>
    public Guid? SourcePlanVersionId { get; set; }

    /// <summary>19.5 — the branch the membership was ADMINISTERED at, taken from the enrolment request (which
    /// already carried it for the plan's branch eligibility rule and then discarded it). Deliberately not "the
    /// member's branch": care happens wherever the member turns up, emr records that on the encounter, and a
    /// second staler answer to the same question is how two reports come to disagree. Null on rows written
    /// before 0013.</summary>
    public Guid? BranchId { get; set; }

    /// <summary>Replay guard. The overlap exclusion makes a double enrolment structurally impossible; this
    /// makes a RETRY return the row the caller already created rather than a 409.</summary>
    public string? IdempotencyKey { get; set; }

    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Inclusive containment — the termination date is still a covered day.</summary>
    public bool Covers(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo.Value);

    /// <summary>True while the member is still serving out a waiting period on <paramref name="date"/>.</summary>
    public bool InWaitingPeriod(DateOnly date) =>
        WaitingPeriodEndsOn is { } ends && date <= ends;

    /// <summary>Live membership: the states the overlap exclusion treats as occupying the beneficiary's slot.
    /// Suspended counts — a suspension pauses the benefit, it does not vacate the membership.</summary>
    public bool IsLive => Status is EnrollmentStatus.Active or EnrollmentStatus.Suspended;
}

/// <summary>
/// Append-only. Every change to a membership is recorded here rather than inferred from the row's current
/// state, which is what makes a RETRO-EFFECTIVE change auditable: the effective date says when it applies,
/// <see cref="OccurredAt"/> says when it was actually decided, and the two are frequently not the same.
/// </summary>
public sealed class EnrollmentEvent
{
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid EnrollmentId { get; set; }
    public EnrollmentEventType EventType { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string? Reason { get; set; }
    public string Payload { get; set; } = "{}";
    public Guid? ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>True when this was back-dated — decided after the date it applies from. Supervisory scope is
    /// required for these, and they are the ones an audit will look at first.</summary>
    public bool IsRetroEffective => EffectiveDate < DateOnly.FromDateTime(OccurredAt.UtcDateTime);
}
