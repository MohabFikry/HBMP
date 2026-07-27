using FluentAssertions;
using Mersal.Provider.Domain;

namespace Mersal.Provider.Tests;

/// <summary>
/// Phase 19.1b — most-specific-wins tier resolution at a SERVICE DATE (design 38 §4.1b).
///
/// Two behaviours here are load-bearing for money. Resolution answers for the service date rather than today,
/// so a provider moving tier in March cannot change what February's already-adjudicated claim was priced at.
/// And it fails SAFE: an unassigned provider is out-of-network, never in-network by omission — the failure
/// that would pay the best negotiated rate to a provider nobody negotiated with.
/// </summary>
public class NetworkTierResolutionTests
{
    private static readonly Guid ProviderId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid LocationId = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid ServiceLineId = Guid.Parse("11111111-0000-0000-0000-000000000003");

    private static readonly NetworkTier T1 = Tier("T1", rank: 1);
    private static readonly NetworkTier T2 = Tier("T2", rank: 2);
    private static readonly NetworkTier Oon = Tier("OON", rank: 99, oon: true);

    private static NetworkTier Tier(string code, int rank, bool oon = false, NetworkTierStatus status = NetworkTierStatus.Active) =>
        new()
        {
            NetworkTierId = Guid.Parse($"22222222-0000-0000-0000-{rank:D12}"),
            TenantId = "t0", TierCode = code, NameEn = code, NameAr = code,
            Rank = rank, IsOutOfNetwork = oon, Status = status,
        };

    private static IReadOnlyDictionary<Guid, NetworkTier> Catalog(params NetworkTier[] tiers) =>
        tiers.ToDictionary(t => t.NetworkTierId, t => t);

    private static ProviderNetworkAssignment Assign(
        NetworkTier tier, NetworkAssignmentScope scope, Guid scopeRef, DateOnly from, DateOnly? to = null,
        NetworkAssignmentStatus status = NetworkAssignmentStatus.Active) => new()
    {
        AssignmentId = Guid.NewGuid(), TenantId = "t0", NetworkTierId = tier.NetworkTierId,
        ProviderId = ProviderId, Scope = scope, ScopeRef = scopeRef,
        EffectiveFrom = from, EffectiveTo = to, Status = status,
    };

    // ---- most-specific-wins ------------------------------------------------------------------------------

    [Fact]
    public void A_location_assignment_overrides_its_parent_providers()
    {
        // The acceptance case from the build prompt: provider in T1, one of its locations in T2 → T2 wins,
        // because the narrower statement is the more deliberate one.
        var assignments = new[]
        {
            Assign(T1, NetworkAssignmentScope.Provider, ProviderId, new(2026, 1, 1)),
            Assign(T2, NetworkAssignmentScope.Location, LocationId, new(2026, 1, 1)),
        };

        var resolved = NetworkTierResolution.Resolve(assignments, Catalog(T1, T2, Oon), new(2026, 6, 15));

        resolved!.Tier.TierCode.Should().Be("T2");
        resolved.Basis.Should().Be(TierResolutionBasis.Location);
    }

    [Fact]
    public void A_contract_service_line_assignment_overrides_both_location_and_provider()
    {
        var assignments = new[]
        {
            Assign(T2, NetworkAssignmentScope.Provider, ProviderId, new(2026, 1, 1)),
            Assign(T2, NetworkAssignmentScope.Location, LocationId, new(2026, 1, 1)),
            Assign(T1, NetworkAssignmentScope.ContractServiceLine, ServiceLineId, new(2026, 1, 1)),
        };

        var resolved = NetworkTierResolution.Resolve(assignments, Catalog(T1, T2, Oon), new(2026, 6, 15));

        resolved!.Tier.TierCode.Should().Be("T1");
        resolved.Basis.Should().Be(TierResolutionBasis.ContractServiceLine);
    }

    [Fact]
    public void The_provider_assignment_applies_when_nothing_narrower_exists()
    {
        var assignments = new[] { Assign(T1, NetworkAssignmentScope.Provider, ProviderId, new(2026, 1, 1)) };

        var resolved = NetworkTierResolution.Resolve(assignments, Catalog(T1, Oon), new(2026, 6, 15));

        resolved!.Tier.TierCode.Should().Be("T1");
        resolved.Basis.Should().Be(TierResolutionBasis.Provider);
    }

    [Fact]
    public void A_more_specific_assignment_that_is_not_in_force_does_not_win()
    {
        // Specificity only breaks ties among assignments that actually govern the date. A location whose T2
        // period ended in May must not keep overriding the provider's live T1 in June.
        var assignments = new[]
        {
            Assign(T1, NetworkAssignmentScope.Provider, ProviderId, new(2026, 1, 1)),
            Assign(T2, NetworkAssignmentScope.Location, LocationId, new(2026, 1, 1), to: new(2026, 5, 1)),
        };

        var resolved = NetworkTierResolution.Resolve(assignments, Catalog(T1, T2, Oon), new(2026, 6, 15));

        resolved!.Tier.TierCode.Should().Be("T1");
        resolved.Basis.Should().Be(TierResolutionBasis.Provider);
    }

    // ---- the service-date boundary, in both directions ---------------------------------------------------

    [Theory]
    [InlineData("2026-02-15", "T2")]   // before the move
    [InlineData("2026-02-28", "T2")]   // the last day the old assignment governs
    [InlineData("2026-03-01", "T1")]   // the exclusive end is the successor's first day
    [InlineData("2026-03-15", "T1")]   // after the move
    public void A_tier_move_takes_effect_exactly_on_its_effective_date(string serviceDate, string expected)
    {
        // The window is half-open [from, to): the old assignment ends ON 1 March and the new one begins the
        // same day, so there is no gap for a service to fall through and no day covered twice.
        var assignments = new[]
        {
            Assign(T2, NetworkAssignmentScope.Provider, ProviderId, new(2026, 1, 1), to: new(2026, 3, 1)),
            Assign(T1, NetworkAssignmentScope.Provider, ProviderId, new(2026, 3, 1)),
        };

        var resolved = NetworkTierResolution.Resolve(assignments, Catalog(T1, T2, Oon), DateOnly.Parse(serviceDate));

        resolved!.Tier.TierCode.Should().Be(expected);
    }

    [Fact]
    public void An_already_adjudicated_service_is_unaffected_by_a_later_tier_move()
    {
        // The claims-facing guarantee, stated as its own test because it is the reason resolution takes a
        // service date at all. February's service still resolves to February's tier after the March move.
        var assignments = new[]
        {
            Assign(Oon, NetworkAssignmentScope.Provider, ProviderId, new(2026, 1, 1), to: new(2026, 3, 1)),
            Assign(T1, NetworkAssignmentScope.Provider, ProviderId, new(2026, 3, 1)),
        };

        var february = NetworkTierResolution.Resolve(assignments, Catalog(T1, Oon), new(2026, 2, 10));

        february!.Tier.TierCode.Should().Be("OON");
        // …and it is an ASSIGNED out-of-network, not the fallback — the two price the same but mean different
        // things, and an adjudication that cannot tell them apart is not explainable.
        february.Basis.Should().Be(TierResolutionBasis.Provider);
    }

    [Fact]
    public void An_assignment_that_has_not_started_yet_does_not_govern()
    {
        var assignments = new[] { Assign(T1, NetworkAssignmentScope.Provider, ProviderId, new(2026, 9, 1)) };

        var resolved = NetworkTierResolution.Resolve(assignments, Catalog(T1, Oon), new(2026, 6, 15));

        resolved!.Basis.Should().Be(TierResolutionBasis.DefaultOutOfNetwork);
    }

    // ---- fail safe ---------------------------------------------------------------------------------------

    [Fact]
    public void An_unassigned_provider_falls_back_to_out_of_network()
    {
        var resolved = NetworkTierResolution.Resolve([], Catalog(T1, Oon), new(2026, 6, 15));

        resolved!.Tier.TierCode.Should().Be("OON");
        resolved.Basis.Should().Be(TierResolutionBasis.DefaultOutOfNetwork);
        resolved.AssignmentId.Should().BeNull("nothing was assigned — there is no assignment to point at");
    }

    [Fact]
    public void A_revoked_assignment_never_governs()
    {
        // Revoked means "this was a mistake and never applied". ENDING an assignment is closing effective_to,
        // which keeps it resolvable for its own window — conflating the two would rewrite priced history.
        var assignments = new[]
        {
            Assign(T1, NetworkAssignmentScope.Provider, ProviderId, new(2026, 1, 1),
                status: NetworkAssignmentStatus.Revoked),
        };

        var resolved = NetworkTierResolution.Resolve(assignments, Catalog(T1, Oon), new(2026, 6, 15));

        resolved!.Basis.Should().Be(TierResolutionBasis.DefaultOutOfNetwork);
    }

    [Fact]
    public void A_retired_out_of_network_tier_is_not_used_as_the_fallback()
    {
        var retiredOon = Tier("OON", rank: 98, oon: true, status: NetworkTierStatus.Retired);

        var resolved = NetworkTierResolution.Resolve([], Catalog(T1, retiredOon), new(2026, 6, 15));

        resolved.Should().BeNull("a retired tier cannot be the safe default, and guessing one would be worse");
    }

    [Fact]
    public void With_no_out_of_network_tier_configured_resolution_declines_rather_than_guesses()
    {
        // The endpoint turns this into a 409 naming the network-administration gap. Returning "in network"
        // here would be the single most expensive default in the system.
        var resolved = NetworkTierResolution.Resolve([], Catalog(T1, T2), new(2026, 6, 15));

        resolved.Should().BeNull();
    }

    [Fact]
    public void The_specificity_ladder_is_strictly_ordered()
    {
        NetworkTierResolution.Specificity(NetworkAssignmentScope.ContractServiceLine)
            .Should().BeGreaterThan(NetworkTierResolution.Specificity(NetworkAssignmentScope.Location));
        NetworkTierResolution.Specificity(NetworkAssignmentScope.Location)
            .Should().BeGreaterThan(NetworkTierResolution.Specificity(NetworkAssignmentScope.Provider));
    }
}
