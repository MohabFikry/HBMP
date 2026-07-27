using Mersal.Policy.Domain;

namespace Mersal.Policy.Api;

// Phase 19.2 + 19.2b request/response contracts for the membership layer (design 38 §3–§4.2).

public sealed record CreatePolicy(
    string PolicyNo, Guid PayerId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, int? MaxMembers);

public sealed record RenewPolicy(
    string PolicyNo, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    /// <summary>Carrying members forward is EXPLICIT. A renewal that silently moved everyone would make the
    /// count of who is covered a side effect nobody reviewed; the response reports how many moved and, per
    /// ADR-0020, which could not be mapped.</summary>
    bool CarryMembersForward);

public sealed record AttachPolicyPlan(
    Guid PlanVersionId, string PlanLabel, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    bool IsDefault, string? EligibilityRule, int? MaxMembers);

public sealed record CreateMemberGroup(
    string GroupCode, string NameEn, string NameAr, string GroupType, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record CreateEnrollment(
    Guid BeneficiaryId,
    Guid PolicyId,
    /// <summary>Optional: resolved from the policy's default plan when absent (19.2b).</summary>
    Guid? PolicyPlanId,
    Guid? GroupId,
    string Relationship,
    Guid? PrincipalEnrollmentId,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    /// <summary>Used only to evaluate a plan's declarative eligibility rule; never stored here.</summary>
    int? AgeYears,
    Guid? BranchId);

public sealed record TerminateEnrollment(DateOnly EffectiveDate, string Reason);
public sealed record ReinstateEnrollment(DateOnly EffectiveDate, string? Reason);
public sealed record ChangeGroup(Guid? GroupId, DateOnly EffectiveDate, string? Reason);
public sealed record ChangePlan(Guid PolicyPlanId, DateOnly EffectiveDate, string Reason);

public sealed record PolicyPlanView(
    Guid PolicyPlanId, Guid PolicyId, Guid PlanVersionId, string PlanLabel,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool IsDefault, string? EligibilityRule,
    int? MaxMembers, string Status, int MemberCount)
{
    public static PolicyPlanView From(PolicyPlan p, int memberCount = 0)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.PolicyPlanId, p.PolicyId, p.PlanVersionId, p.PlanLabel, p.EffectiveFrom, p.EffectiveTo,
            p.IsDefault, p.EligibilityRule, p.MaxMembers, p.Status.ToString(), memberCount);
    }
}

public sealed record MemberGroupView(
    Guid GroupId, Guid PolicyId, string GroupCode, string NameEn, string NameAr, string GroupType,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status)
{
    public static MemberGroupView From(MemberGroup g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return new(g.GroupId, g.PolicyId, g.GroupCode, g.NameEn, g.NameAr, g.GroupType.ToString(),
            g.EffectiveFrom, g.EffectiveTo, g.Status.ToString());
    }
}

/// <summary><c>CoveragesGenerated</c> is reported rather than left to be discovered: an enrolment whose plan
/// version covered nothing would otherwise succeed silently and produce a member entitled to nothing.</summary>
public sealed record EnrollmentView(
    Guid EnrollmentId, Guid BeneficiaryId, Guid PolicyId, Guid PolicyPlanId, Guid? GroupId,
    string MemberNo, string Relationship, Guid? PrincipalEnrollmentId,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, DateOnly? WaitingPeriodEndsOn,
    string Status, string? TerminationReason, Guid? SourcePlanVersionId, int CoveragesGenerated)
{
    public static EnrollmentView From(Enrollment e, int coveragesGenerated = 0)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.EnrollmentId, e.BeneficiaryId, e.PolicyId, e.PolicyPlanId, e.GroupId,
            e.MemberNo, e.Relationship.ToString(), e.PrincipalEnrollmentId,
            e.EffectiveFrom, e.EffectiveTo, e.WaitingPeriodEndsOn,
            e.Status.ToString(), e.TerminationReason, e.SourcePlanVersionId, coveragesGenerated);
    }
}

public sealed record EnrollmentEventView(
    Guid EventId, string EventType, DateOnly EffectiveDate, string? Reason,
    DateTimeOffset OccurredAt, Guid? ActorUserId, bool RetroEffective)
{
    public static EnrollmentEventView From(EnrollmentEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.EventId, e.EventType.ToString(), e.EffectiveDate, e.Reason,
            e.OccurredAt, e.ActorUserId, e.IsRetroEffective);
    }
}

/// <summary>The result of a plan change: what each category's limit and remaining balance became once
/// consumption was carried across (ADR-0020). Reported so the officer can see the arithmetic BEFORE confirming
/// — a member who has used 300 of 1,000 moving to a 500 plan has 200 left, not 500, and that has to be
/// visible rather than discovered at the next visit.</summary>
public sealed record PlanChangeView(
    Guid EnrollmentId, Guid PolicyPlanId, Guid PlanVersionId, string ConsumptionPolicy,
    IReadOnlyList<CarriedLimitView> CarriedLimits);

public sealed record CarriedLimitView(
    Guid BenefitCategoryId, decimal? LimitValue, decimal ConsumedValue, decimal? Remaining, bool Exhausted)
{
    public static CarriedLimitView From(CarriedLimit c) =>
        new(c.BenefitCategoryId, c.LimitValue, c.ConsumedValue, c.Remaining, c.Exhausted);
}

public sealed record RenewalView(
    Guid PolicyId, string PolicyNo, Guid? PreviousPolicyId, int MembersCarried, IReadOnlyList<string> Unmapped);
