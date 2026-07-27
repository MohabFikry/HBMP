namespace Mersal.Provider.Domain;

// Phase 19.1b — network tiers and effective-dated provider tier assignment (design 38 §3, §4.1b).
// Owned by the Network Team. policy-service consumes the RESOLVED tier to price a benefit; it never writes here.

/// <summary>What a tier assignment attaches to. The order of these values is not the resolution order —
/// see <see cref="NetworkTierResolution.Specificity"/>, which is the single place that ranking is stated.</summary>
public enum NetworkAssignmentScope { Provider, Location, ContractServiceLine }

/// <summary>Tier lifecycle. <c>Retired</c> tiers stay readable: a claim adjudicated last year was priced at a
/// tier that may no longer be offered, and that history must still render.</summary>
public enum NetworkTierStatus { Active, Retired }

/// <summary><c>Revoked</c> means "this assignment was a mistake and never governed anything". ENDING an
/// assignment is not a revocation — it is closing <c>EffectiveTo</c>, which keeps the row resolvable for
/// service dates inside its window. Conflating the two would let a tier move rewrite adjudicated history.</summary>
public enum NetworkAssignmentStatus { Active, Revoked }

/// <summary>A network tier: T1 preferred, T2 standard, OON out-of-network (or Gold/Silver/Bronze).
/// <see cref="Rank"/> orders them, 1 being most preferred.</summary>
public sealed class NetworkTier
{
    public Guid NetworkTierId { get; set; }
    public string TenantId { get; set; } = default!;
    public string TierCode { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public int Rank { get; set; }
    public string? Description { get; set; }

    /// <summary>Marks the tier resolution falls back to when a provider has no assignment at all. Exactly one
    /// Active tier may carry this (partial unique index in 0008) — the fallback has to be unambiguous or
    /// "fail safe" becomes "fail to whichever row came back first".</summary>
    public bool IsOutOfNetwork { get; set; }

    public NetworkTierStatus Status { get; set; } = NetworkTierStatus.Active;
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Places a provider, one of its locations, or a single contract service line into a tier for a date
/// window. The window is half-open <c>[EffectiveFrom, EffectiveTo)</c> — a successor starts on exactly the day
/// its predecessor ends.</summary>
public sealed class ProviderNetworkAssignment
{
    public Guid AssignmentId { get; set; }
    public string TenantId { get; set; } = default!;
    public Guid NetworkTierId { get; set; }

    /// <summary>Denormalized owning provider (for Provider scope it equals <see cref="ScopeRef"/>). Carried so
    /// the row can take the same provider-scoped RLS predicate as the rest of the schema.</summary>
    public Guid ProviderId { get; set; }

    public NetworkAssignmentScope Scope { get; set; }
    public Guid ScopeRef { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    /// <summary>EXCLUSIVE end of the window; null = open-ended.</summary>
    public DateOnly? EffectiveTo { get; set; }
    public NetworkAssignmentStatus Status { get; set; } = NetworkAssignmentStatus.Active;
    public string? RevokedReason { get; set; }
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Half-open containment: the start day is governed by this assignment, the end day by its
    /// successor.</summary>
    public bool InForce(DateOnly serviceDate) =>
        Status == NetworkAssignmentStatus.Active && !IsDeleted
        && EffectiveFrom <= serviceDate
        && (EffectiveTo is null || serviceDate < EffectiveTo.Value);
}

/// <summary>Why a tier was chosen. Returned to callers so an adjudication can be explained after the fact
/// ("out-of-network because nothing was assigned" reads very differently from "assigned to OON").</summary>
public enum TierResolutionBasis
{
    /// <summary>A contract service line carried its own assignment — the most specific statement available.</summary>
    ContractServiceLine,
    /// <summary>The performing location carried an assignment, overriding its parent provider.</summary>
    Location,
    /// <summary>The provider's own assignment applied.</summary>
    Provider,
    /// <summary>Nothing matched. The out-of-network tier was applied as the safe default.</summary>
    DefaultOutOfNetwork,
}

/// <summary>The resolved tier plus the reason it won.</summary>
public sealed record TierResolution(NetworkTier Tier, TierResolutionBasis Basis, Guid? AssignmentId);

/// <summary>
/// Phase 19.1b — most-specific-wins tier resolution AT A SERVICE DATE.
///
/// Pure and side-effect-free so the whole matrix is table-testable. Two properties matter more than the code:
/// resolution is done for the SERVICE date rather than today (a provider moving tier in March must not change
/// what February's claim was priced at), and it FAILS SAFE — an unrecognised provider is out-of-network, never
/// silently in-network by omission.
/// </summary>
public static class NetworkTierResolution
{
    /// <summary>The specificity ladder, stated once. A location assignment overrides its parent provider, and a
    /// contract service line overrides both, because the narrower statement is the more deliberate one.</summary>
    public static int Specificity(NetworkAssignmentScope scope) => scope switch
    {
        NetworkAssignmentScope.ContractServiceLine => 3,
        NetworkAssignmentScope.Location => 2,
        NetworkAssignmentScope.Provider => 1,
        _ => 0,
    };

    /// <summary>The assignment that governs <paramref name="serviceDate"/>, or null when none does.
    /// <paramref name="candidates"/> is expected to already be restricted to the provider, location and service
    /// lines in play — this function decides WHICH of them wins, not which are relevant.</summary>
    public static ProviderNetworkAssignment? MostSpecific(
        IEnumerable<ProviderNetworkAssignment> candidates, DateOnly serviceDate)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(a => a.InForce(serviceDate))
            .OrderByDescending(a => Specificity(a.Scope))
            // The 0008 exclusion constraint already guarantees one in-force row per (scope, scope_ref), so a
            // tie here can only come from two DIFFERENT refs at the same level. Ordering by start date then id
            // keeps the answer stable rather than planner-dependent.
            .ThenByDescending(a => a.EffectiveFrom)
            .ThenBy(a => a.AssignmentId)
            .FirstOrDefault();
    }

    /// <summary>The full decision: the winning assignment's tier, or the out-of-network default.</summary>
    /// <param name="tiers">Tiers by id — the candidates' tiers plus the out-of-network default.</param>
    /// <returns>null only when no assignment matched AND no Active out-of-network tier is configured, which is
    /// a network-administration gap the caller must surface rather than paper over with a guess.</returns>
    public static TierResolution? Resolve(
        IEnumerable<ProviderNetworkAssignment> candidates,
        IReadOnlyDictionary<Guid, NetworkTier> tiers,
        DateOnly serviceDate)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        var winner = MostSpecific(candidates, serviceDate);
        if (winner is not null && tiers.TryGetValue(winner.NetworkTierId, out var tier))
        {
            var basis = winner.Scope switch
            {
                NetworkAssignmentScope.ContractServiceLine => TierResolutionBasis.ContractServiceLine,
                NetworkAssignmentScope.Location => TierResolutionBasis.Location,
                _ => TierResolutionBasis.Provider,
            };
            return new TierResolution(tier, basis, winner.AssignmentId);
        }

        // Fail safe. An unassigned provider is out-of-network; the alternative — treating "not configured" as
        // "in network" — silently pays the best rate to a provider nobody negotiated with.
        var fallback = tiers.Values.FirstOrDefault(t =>
            t.IsOutOfNetwork && t.Status == NetworkTierStatus.Active && !t.IsDeleted);
        return fallback is null ? null : new TierResolution(fallback, TierResolutionBasis.DefaultOutOfNetwork, null);
    }
}
