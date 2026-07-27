using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;

namespace Mersal.Policy.Api;

// Phase 19.5 — the response shapes for policy query, member query, coverage details and the administrative 360.
//
// ============================================================================================================
// THERE IS NO CLINICAL FIELD IN THIS FILE EITHER
// ============================================================================================================
// Same argument as 19.4, and the same reflection test enforces it across both. What these payloads carry is
// membership, entitlement and money; a diagnosis has nowhere to go. The one place clinical content can reach a
// caller through this surface is a NOTE body, and that goes through NoteVisibilityRules — which withholds the
// body while still admitting the note exists, so an officer knows to ask someone entitled rather than
// concluding nothing was written.
//
// WHAT *IS* PROJECTED BY ROLE HERE:
//   · amounts (limit / consumed / remaining in money)   → the 19.4 AmountReaders line, unchanged
//   · terminationReason                                 → case-handling roles, not the front desk
//   · payer + contract terms (maxMembers, plan counts)  → administration and the money roles
// Reception and the call centre keep everything that answers "is this person covered, for what, and from
// when" — which is their whole job — and lose the commercial and case-handling fields around it.

/// <summary>One policy in a query result.</summary>
public sealed record PolicyQueryRowView(
    Guid PolicyId,
    string PolicyNo,
    Guid? PayerId,
    string Status,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    int MemberCount,
    string MemberCountBand,
    int? MaxMembers,
    int PlanCount,
    decimal? TotalLimit,
    decimal? TotalConsumed,
    decimal? PercentUsed,
    string UtilizationBand)
{
    public static PolicyQueryRowView From(PolicyQueryRow r, bool mayReadAmounts, bool mayReadContract)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(
            r.PolicyId, r.PolicyNo,
            mayReadContract ? r.PayerId : null,
            r.Status.ToString(), r.EffectiveFrom, r.EffectiveTo,
            r.MemberCount, r.CountBand.ToString(),
            mayReadContract ? r.MaxMembers : null,
            r.PlanCount,
            mayReadAmounts ? r.TotalLimit : null,
            mayReadAmounts ? r.TotalConsumed : null,
            // The PERCENTAGE survives a caller who may not see the amounts. "This policy is at 92% of its
            // ceiling" is an operational fact; the ceiling in pounds is a commercial one.
            r.PercentUsed,
            r.Band.ToString());
    }
}

/// <summary>One membership in a query result. <see cref="GivenName"/>/<see cref="FamilyName"/> come from
/// patient-service for THIS PAGE only, and stay null when it could not be asked — a blank name is legible, a
/// wrong one is not.</summary>
public sealed record MemberQueryRowView(
    Guid EnrollmentId,
    Guid BeneficiaryId,
    string MemberNo,
    string? GivenName,
    string? FamilyName,
    string? BeneficiaryStatus,
    Guid PolicyId,
    Guid PolicyPlanId,
    string? PlanLabel,
    Guid? GroupId,
    Guid? PayerId,
    string Relationship,
    string Status,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    DateOnly? WaitingPeriodEndsOn,
    string WaitingPeriodState,
    Guid? BranchId,
    string? TerminationReason,
    decimal? TotalLimit,
    decimal? TotalConsumed,
    decimal? TotalRemaining,
    decimal? PercentUsed,
    string UtilizationBand)
{
    public static MemberQueryRowView From(
        MemberQueryRow r, BeneficiarySummary? summary, DateOnly asOf,
        bool mayReadAmounts, bool mayReadContract, bool mayReadCase)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(
            r.EnrollmentId, r.BeneficiaryId, r.MemberNo,
            summary?.GivenName, summary?.FamilyName, summary?.Status,
            r.PolicyId, r.PolicyPlanId, r.PlanLabel, r.GroupId,
            mayReadContract ? r.PayerId : null,
            r.Relationship.ToString(), r.Status.ToString(),
            r.EffectiveFrom, r.EffectiveTo, r.WaitingPeriodEndsOn, r.WaitingPeriod(asOf).ToString(),
            r.BranchId,
            mayReadCase ? r.TerminationReason : null,
            mayReadAmounts ? r.TotalLimit : null,
            mayReadAmounts ? r.TotalConsumed : null,
            mayReadAmounts ? Math.Max(0m, r.TotalLimit - r.TotalConsumed) : null,
            r.PercentUsed,
            r.Band.ToString());
    }
}

/// <summary>A page of results plus the counts and flags that make a short page legible.</summary>
public sealed record QueryPageView<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    string SortedBy,
    /// <summary>True when the caller's payer restriction narrowed the result set. Without this a payer-scoped
    /// user reads "12 policies" as "Mersal has 12 policies".</summary>
    bool PayerScopeApplied,
    /// <summary>True when a name/identifier filter hit the resolver's cap. The page is then a SUBSET of the
    /// matches, and saying so is the difference between a search and a wrong answer.</summary>
    bool IdentityMatchTruncated,
    /// <summary>Services that could not be reached while composing this page (names, typically). Their fields
    /// are null rather than absent.</summary>
    IReadOnlyList<string> Unavailable);

/// <summary>The 360's sections. Every one is nullable and <see cref="Unavailable"/> names what could not be
/// composed — the same "null is not zero" rule 19.4 established for cross-service facts.</summary>
public sealed record AdministrativeThreeSixtyView(
    Guid BeneficiaryId,
    DateOnly AsOf,
    /// <summary>patient-service's own answer, passed through exactly as it projected it for this caller.</summary>
    IReadOnlyDictionary<string, object?>? Beneficiary,
    IReadOnlyList<MembershipSummaryView> Memberships,
    IReadOnlyList<CoveredFamilyMemberView> CoveredFamily,
    IReadOnlyList<EnrollmentHistoryView> EnrollmentHistory,
    IReadOnlyList<DocumentSummaryView> Documents,
    IReadOnlyList<NoteView> Notes,
    IReadOnlyList<string> Unavailable,
    /// <summary>Sections withheld because the caller's payer scope does not cover the policy behind them.
    /// Named rather than silently dropped: a 360 that looks complete but is not is worse than one that says so.</summary>
    IReadOnlyList<string> Withheld);

public sealed record MembershipSummaryView(
    Guid EnrollmentId, string MemberNo, Guid PolicyId, string? PolicyNo, Guid? PayerId,
    Guid PolicyPlanId, string? PlanLabel, Guid? GroupId, string? GroupCode,
    string Relationship, string Status, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    DateOnly? WaitingPeriodEndsOn, string WaitingPeriodState, Guid? BranchId, string? TerminationReason);

/// <summary>Dependants covered under this member, or the principal they are covered under. The COVERED family,
/// which is a membership fact policy-service owns — deliberately not patient-service's household, which
/// answers a different question and would disagree the moment a relative is not enrolled.</summary>
public sealed record CoveredFamilyMemberView(
    Guid EnrollmentId, Guid BeneficiaryId, string MemberNo, string Relationship, string Status,
    bool IsPrincipal);

public sealed record EnrollmentHistoryView(
    Guid EventId, Guid EnrollmentId, string EventType, DateOnly EffectiveDate, DateTimeOffset OccurredAt,
    bool IsRetroEffective, string? Reason);

/// <summary>Document METADATA only — the 19.3b link, never the bytes. <see cref="ContentAccessible"/> is
/// projected rather than left to the client to infer, so the UI's download affordance and the API's 403 cannot
/// disagree.</summary>
public sealed record DocumentSummaryView(
    Guid LinkId, Guid DocumentId, string DocumentClass, string VisibilityClass, string Title,
    DateOnly? DocumentDate, DateTimeOffset UploadedAt, string UploadedByDisplay, string Status,
    bool ContentAccessible);

/// <summary>
/// Which roles read what, on this surface.
///
/// <para>The amount line is <b>the same list 19.4 uses</b>, and that is the point: a member's spend is one fact
/// whether it arrives as a utilization total, a Financial note or a query column. Three lists would mean three
/// answers to "may this role see the money", which is how a min-necessary rule quietly stops meaning
/// anything.</para>
/// </summary>
public static class AdministrativeProjection
{
    /// <summary>Money. Identical to <c>UtilizationProjection.AmountReaders</c> by intent.</summary>
    private static readonly string[] AmountReaders =
    [
        "finance", "claims_officer", "beneficiary_mgmt", "beneficiary_mgmt_supervisor",
        "policy_admin", "org_admin", "super_admin", "medical_director", "network_team",
    ];

    /// <summary>Commercial terms — who the payer is, what the contract caps at. Administration and the money
    /// roles. A receptionist confirming cover does not need to know which donor funds it, and a payer name on a
    /// front-desk screen is the kind of detail that ends up spoken aloud in a waiting room.</summary>
    private static readonly string[] ContractReaders =
    [
        "finance", "claims_officer", "beneficiary_mgmt", "beneficiary_mgmt_supervisor",
        "policy_admin", "org_admin", "super_admin", "network_team", "medical_director",
    ];

    /// <summary>Case-handling detail — above all a termination reason, which can say "deceased", "left the
    /// programme" or "suspected misuse". Every one of those is something a member should hear from the person
    /// handling their case, not read off a search result at a busy desk.</summary>
    private static readonly string[] CaseReaders =
    [
        "beneficiary_mgmt", "beneficiary_mgmt_supervisor", "policy_admin", "org_admin", "super_admin",
        "case_manager", "medical_director", "claims_officer", "finance",
    ];

    public static bool MayReadAmounts(IReadOnlyCollection<string> roles) => Any(roles, AmountReaders);

    public static bool MayReadContract(IReadOnlyCollection<string> roles) => Any(roles, ContractReaders);

    public static bool MayReadCase(IReadOnlyCollection<string> roles) => Any(roles, CaseReaders);

    private static bool Any(IReadOnlyCollection<string> roles, string[] readers)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(r => readers.Contains(r, StringComparer.Ordinal));
    }
}
