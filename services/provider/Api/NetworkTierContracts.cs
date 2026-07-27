using Mersal.Provider.Domain;

namespace Mersal.Provider.Api;

// Phase 19.1b request/response contracts for network administration (design 38 §3, §4.1b).

public sealed record CreateNetworkTier(
    string TierCode, string NameEn, string NameAr, int Rank, string? Description, bool IsOutOfNetwork);

/// <summary>Labels and rank only. <c>tierCode</c> and <c>isOutOfNetwork</c> are deliberately absent: both are
/// referenced by policy.benefit_rule_tier and by already-adjudicated claims, so changing them would rewrite
/// what history meant rather than correct a typo. Retire the tier and create the right one.</summary>
public sealed record UpdateNetworkTier(string? NameEn, string? NameAr, int? Rank, string? Description);

public sealed record RetireNetworkTier(string Reason);

public sealed record CreateTierAssignment(string Scope, Guid ScopeRef, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record NetworkTierView(
    Guid NetworkTierId, string TierCode, string NameEn, string NameAr, int Rank,
    string? Description, bool IsOutOfNetwork, string Status)
{
    public static NetworkTierView From(NetworkTier t) =>
        new(t.NetworkTierId, t.TierCode, t.NameEn, t.NameAr, t.Rank, t.Description, t.IsOutOfNetwork, t.Status.ToString());
}

public sealed record TierAssignmentView(
    Guid AssignmentId, Guid NetworkTierId, string? TierCode, Guid ProviderId, string Scope, Guid ScopeRef,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status)
{
    public static TierAssignmentView From(ProviderNetworkAssignment a, string? tierCode) =>
        new(a.AssignmentId, a.NetworkTierId, tierCode, a.ProviderId, a.Scope.ToString(), a.ScopeRef,
            a.EffectiveFrom, a.EffectiveTo, a.Status.ToString());
}

/// <summary>The resolver's answer. <c>basis</c> is part of the contract, not decoration: "out-of-network
/// because nothing was assigned" and "assigned to the out-of-network tier" produce the same price and need
/// very different follow-up, and an adjudication that cannot say which is not explainable.</summary>
public sealed record TierResolutionView(
    Guid NetworkTierId, string TierCode, string NameEn, string NameAr, int Rank, bool IsOutOfNetwork,
    string Basis, Guid? AssignmentId, Guid ProviderId, Guid? LocationId, string? ServiceCode, DateOnly ServiceDate)
{
    public static TierResolutionView From(
        TierResolution r, Guid providerId, Guid? locationId, string? serviceCode, DateOnly serviceDate) =>
        new(r.Tier.NetworkTierId, r.Tier.TierCode, r.Tier.NameEn, r.Tier.NameAr, r.Tier.Rank,
            r.Tier.IsOutOfNetwork, r.Basis.ToString(), r.AssignmentId,
            providerId, locationId, serviceCode, serviceDate);
}
