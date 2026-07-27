using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;

namespace Mersal.Policy.Api;

// Phase 19.4 — the utilization response shapes (design 38 §4.3).
//
// ============================================================================================================
// THERE IS NO CLINICAL FIELD IN THIS FILE, AND THAT IS THE POINT.
// ============================================================================================================
// Utilization is read by Finance, the Network Team and Beneficiary Management — three role families that must
// never receive a diagnosis (11-permission-matrix; design 38 §4.3). Rather than carry clinical values and
// strip them per role, the payload has nowhere to put one: the counts come from emr as counts, the money comes
// from claims (whose schema has no clinical column at all), and the categories are the five-value benefit
// vocabulary, not procedures.
//
// A reflection test asserts this for every role, so the guarantee survives someone later adding a "convenient"
// field. Structural absence beats a filter, because a filter has to be remembered and a missing field cannot
// be forgotten.

/// <summary>One benefit category's accumulator: what was agreed, what is gone, what is left.</summary>
public sealed record CategoryUtilizationView(
    string BenefitCategory,
    string? LimitType,
    decimal? Limit,
    decimal Consumed,
    decimal? Remaining,
    decimal? PercentUsed,
    bool Unlimited,
    string CurrencyCode,
    string ResetPeriod,
    DateOnly? ResetsOn,
    decimal? WindowActivity,
    int WindowEvents)
{
    public static CategoryUtilizationView From(CategoryAccumulator a, CategoryActivity? activity) =>
        new(a.BenefitCategoryCode, a.LimitType?.ToString(), a.LimitValue, a.ConsumedValue, a.Remaining,
            a.PercentUsed, a.IsUnlimited, a.CurrencyCode, a.ResetPeriod.ToString(), a.ResetsOn,
            activity?.NetQuantity, activity?.EventCount ?? 0);
}

/// <summary>The tier split. <c>Attributed=false</c> is the honest bucket for movements whose provider is
/// unknown — never folded into in-network, which would flatter the network on the one number it is judged
/// by.</summary>
public sealed record TierUtilizationView(
    string TierCode, bool OutOfNetwork, bool Attributed, decimal NetQuantity, int Events)
{
    public static TierUtilizationView From(TierUtilization t) =>
        new(t.TierCode, t.IsOutOfNetwork, t.IsAttributed, t.NetQuantity, t.EventCount);
}

/// <summary>
/// Facts owned by other services. Every figure is nullable, and null means "could not ask", not "zero".
/// <see cref="Unavailable"/> names the services that did not answer, so a blank is legible rather than
/// mistaken for a member who used nothing.
/// </summary>
public sealed record ExternalUtilizationView(
    int? Encounters,
    int? AuthorizationsRaised,
    int? AuthorizationsApproved,
    int? AuthorizationsDenied,
    decimal? ClaimedAmount,
    decimal? ApprovedAmount,
    decimal? MemberShareAmount,
    string CurrencyCode,
    IReadOnlyList<string> Unavailable)
{
    public static ExternalUtilizationView From(UtilizationFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var e = f.External;
        return new(e.EncounterCount, e.AuthorizationsRaised, e.AuthorizationsApproved, e.AuthorizationsDenied,
            e.ClaimedAmount, e.ApprovedAmount, e.MemberShareAmount, e.CurrencyCode, f.UnavailableSources);
    }

    /// <summary>
    /// The money removed for a caller with no financial entitlement, counts kept.
    ///
    /// Encounter and authorization COUNTS are operational — a Beneficiary Management officer needs them to
    /// answer "has this member been seen". Amounts are <c>financials</c> and stop at Finance, Claims and
    /// administration, the same line <c>NoteVisibilityRules</c> draws for a Financial note.
    /// </summary>
    public ExternalUtilizationView WithoutAmounts() =>
        this with { ClaimedAmount = null, ApprovedAmount = null, MemberShareAmount = null };
}

/// <summary>
/// The reconciliation statement, carried in every response.
///
/// The prompt asks for a test; this is the test's invariant asserted at RUNTIME as well, because a report is
/// read on days no test runs. <see cref="Reconciled"/> false means the two paths disagree — which must be
/// visible on the report rather than discovered later by whoever acted on it.
/// </summary>
public sealed record ReconciliationView(decimal AccumulatorTotal, decimal ReportedTotal, bool Reconciled)
{
    public static ReconciliationView Of(decimal accumulator, decimal reported) =>
        new(accumulator, reported, accumulator == reported);
}

/// <summary>One member's utilization.</summary>
public sealed record MemberUtilizationView(
    Guid BeneficiaryId,
    Guid EnrollmentId,
    string MemberNo,
    DateOnly AsOf,
    DateOnly WindowFrom,
    DateOnly WindowTo,
    IReadOnlyList<CategoryUtilizationView> Categories,
    IReadOnlyList<TierUtilizationView> ByNetworkTier,
    ExternalUtilizationView External,
    ReconciliationView Reconciliation);

/// <summary>A row in a scope's per-member table.</summary>
public sealed record MemberRowView(
    Guid BeneficiaryId, Guid EnrollmentId, string MemberNo, Guid PolicyPlanId, Guid? GroupId,
    decimal TotalLimit, decimal TotalConsumed, decimal TotalRemaining, decimal? PercentUsed, bool AnyUnlimited)
{
    public static MemberRowView From(MemberUtilization m) =>
        new(m.BeneficiaryId, m.EnrollmentId, m.MemberNo, m.PolicyPlanId, m.GroupId,
            m.TotalLimit, m.TotalConsumed, m.TotalRemaining, m.PercentUsed, m.AnyUnlimited);
}

public sealed record DistributionBucketView(string Label, int MemberCount)
{
    public static DistributionBucketView From(DistributionBucket b) => new(b.Label, b.MemberCount);
}

/// <summary>A group / plan / policy / payer aggregate.</summary>
public sealed record ScopeUtilizationView(
    string Scope,
    Guid ScopeId,
    DateOnly AsOf,
    DateOnly WindowFrom,
    DateOnly WindowTo,
    int MemberCount,
    decimal TotalLimit,
    decimal TotalConsumed,
    decimal TotalRemaining,
    decimal? PercentUsed,
    decimal OutlierThresholdPercent,
    IReadOnlyList<MemberRowView> Members,
    IReadOnlyList<MemberRowView> Outliers,
    IReadOnlyList<DistributionBucketView> Distribution,
    IReadOnlyList<TierUtilizationView> ByNetworkTier,
    ExternalUtilizationView External,
    ReconciliationView Reconciliation);

/// <summary>
/// Which roles read the money on a utilization report.
///
/// Deliberately the same list as a Financial note's readers (19.3): a member's spend is the same fact whether
/// it arrives as a note or as a total, and two different answers to "may this role see the amount" is how a
/// min-necessary rule quietly stops meaning anything.
/// </summary>
public static class UtilizationProjection
{
    private static readonly string[] AmountReaders =
    [
        "finance", "claims_officer", "beneficiary_mgmt", "beneficiary_mgmt_supervisor",
        "policy_admin", "org_admin", "super_admin", "medical_director", "network_team",
    ];

    public static bool MayReadAmounts(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(r => AmountReaders.Contains(r, StringComparer.Ordinal));
    }

    public static ExternalUtilizationView Project(ExternalUtilizationView view, IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(view);
        return MayReadAmounts(roles) ? view : view.WithoutAmounts();
    }
}
