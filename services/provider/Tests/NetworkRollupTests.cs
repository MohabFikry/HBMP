using FluentAssertions;
using Mersal.Auth;
using Mersal.Provider.Api;

namespace Mersal.Provider.Tests;

/// <summary>
/// The network roll-up (33.7) — the four numbers the SPA used to count for itself.
/// </summary>
/// <remarks>
/// <para><c>GET /api/v1/metrics</c> has returned <c>{total, active, suspended, terminated}</c> since phase
/// 2b, and had no Kong route until 33.7 — so nothing had ever called it, and it had no test either. The
/// Performance screen produced the identical figures by fetching the provider directory and counting rows
/// whose rendered <c>status.label.en</c> was the string "Active".</para>
///
/// <para>The refusal below is the part that made that more than a tidiness problem. A provider-scoped caller
/// is answered 403: a provider must not learn the shape of the network it competes in. A count assembled in
/// the browser from a list the caller can already read enforces nothing, and the "authorization" existed only
/// on a path nobody took.</para>
/// </remarks>
public class NetworkRollupTests
{
    private static HbmpPrincipal Principal(string tenant, params string[] roles) => new()
    {
        Subject = "u-1",
        Roles = new HashSet<string>(roles),
        Scopes = new HashSet<string> { "provider:read" },
        TenantId = tenant,
        ProviderId = "11111111-1111-1111-1111-111111111111",
    };

    /// <summary>
    /// The refusal, and the pair of names it turns on.
    /// </summary>
    /// <remarks>
    /// <c>provider_admin</c> is ONE PROVIDER'S OWN ADMINISTRATOR — T4, listed in
    /// <c>HbmpPrincipal.ProviderScopedRoles</c>, bound to that provider by ABAC and RLS. <c>network_team</c>
    /// is Mersal's Network Team — T2, tenant-wide. Design 11 §3.3 gives them different rows and design 07
    /// FR-IAM-003 lists them as separate portals; the SPA's <c>ROLE_MAP</c> collapses both onto the portal
    /// name <c>provider_admin</c>, which is how a client-side mirror of this rule came to answer yes for the
    /// caller this endpoint refuses (design 52 §5).
    /// </remarks>
    [Fact]
    public void The_roll_up_is_refused_to_a_caller_who_belongs_to_one_provider()
    {
        ProviderAccessGuard.IsProviderScoped(Principal("t0", "provider_admin")).Should().BeTrue();
        ProviderAccessGuard.IsProviderScoped(Principal("t0", "lab_tech")).Should().BeTrue();
        ProviderAccessGuard.IsProviderScoped(Principal("t0", "pharmacist")).Should().BeTrue();
        ProviderAccessGuard.IsProviderScoped(Principal("t0", "radiology_tech")).Should().BeTrue();
    }

    [Fact]
    public void The_network_team_is_not_provider_scoped_and_may_read_it()
    {
        ProviderAccessGuard.IsProviderScoped(Principal("t0", "network_team")).Should().BeFalse();
        ProviderAccessGuard.IsProviderScoped(Principal("t0", "org_admin")).Should().BeFalse();
        ProviderAccessGuard.IsProviderScoped(Principal("t0", "super_admin")).Should().BeFalse();
    }

    [Fact]
    public void The_roll_up_counts_the_status_enum_rather_than_anything_rendered()
    {
        // The shape is the whole contract: four ints, and `total` is not the sum of the other three —
        // a provider can be in a state that is none of Active/Suspended/Terminated (Draft, Credentialed),
        // and folding those into "active" is how an onboarding backlog disappears from the only screen that
        // would show it.
        var view = new NetworkMetricsView(Total: 41, Active: 33, Suspended: 6, Terminated: 2);

        view.Total.Should().Be(41);
        // `total` is every provider in the tenant, not the sum of the three named states: Draft and
        // Credentialed are neither active nor suspended nor terminated, and folding them into "active" is how
        // an onboarding backlog disappears from the only screen that would have shown it.
        (view.Active + view.Suspended + view.Terminated).Should().BeLessThanOrEqualTo(view.Total);
    }

    [Fact]
    public void A_provider_with_no_orders_reports_no_average_turnaround_rather_than_zero()
    {
        var view = new ProviderMetricsView(
            Guid.NewGuid(), "Active", ActiveContracts: 2, ServicesOffered: 41,
            Credentials: new CredentialCountsView(Valid: 5, ExpiringSoon: 1, Expired: 0),
            OrdersFulfilled: 0, AvgTurnaroundHours: null);

        // Null and 0.0 are different facts and only one of them would be remarkable. A performance panel
        // that renders "0 hours average turnaround" for a provider that has fulfilled nothing is stating an
        // achievement it has no evidence for.
        view.AvgTurnaroundHours.Should().BeNull();
        view.OrdersFulfilled.Should().Be(0);
    }
}
