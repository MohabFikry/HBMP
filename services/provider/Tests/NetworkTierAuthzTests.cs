using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Provider.Tests;

/// <summary>
/// Phase 19.1b authorization proof — the separation network administration exists to create (design 38 §4.1b).
///
/// Deciding WHICH tier a hospital sits in is network commercial policy: the Network Team negotiates it. Deciding
/// what a member PAYS at a tier is benefit design: policy administration owns it (policy.benefit_rule_tier).
/// Collapsing the two would let one person set the out-of-network penalty AND decide who is out of network,
/// which is both an SoD failure and a straightforward way to reprice the whole network by accident.
///
/// Run against the real engine over <see cref="ProviderPolicies"/>, so this is the rule itself under test.
/// </summary>
public class NetworkTierAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(ProviderPolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("provider-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Res(string tenant = "t0") => new() { Type = "network_tier", TenantId = tenant };

    private Task<AuthzDecision> Decide(HbmpPrincipal p, string tenant = "t0") =>
        Engine().EvaluateAsync(new AuthzRequest(p, ProviderPolicies.Actions.NetworkAdmin, Res(tenant)));

    [Theory]
    [InlineData("network_team")]
    [InlineData("org_admin")]
    [InlineData("super_admin")]
    public async Task The_network_team_may_administer_tiers(string role)
    {
        var d = await Decide(Principal(role, "provider:admin"));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_policy_administrator_may_not_create_or_reassign_a_tier()
    {
        // THE acceptance criterion. A policy admin configures cost-share PER tier and must not be able to move
        // a provider between them — with either the role or the scope they legitimately hold.
        var withPolicyAuthority = await Decide(Principal("policy_admin", "policy:admin", "provider:read"));
        withPolicyAuthority.IsAllowed.Should().BeFalse();

        // And holding the network scope without the Network Team role is not enough either — the rule needs both.
        var withScopeOnly = await Decide(Principal("policy_admin", "provider:admin"));
        withScopeOnly.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Ordinary_provider_write_authority_does_not_reach_tier_administration()
    {
        // provider:write covers adding a location or recording a credential. A tier move reprices every plan
        // that references the tier, for every member, from its effective date — a different order of act, so
        // it needs its own scope rather than riding along with routine metadata edits.
        var d = await Decide(Principal("network_team", "provider:write", "provider:read"));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task A_provider_admin_may_not_move_their_own_provider_up_a_tier()
    {
        // The self-dealing case: a provider user editing their own network standing.
        var d = await Decide(Principal("provider_admin", "provider:admin", "provider:read"));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Tier_administration_is_tenant_scoped()
    {
        var d = await Decide(Principal("network_team", "provider:admin"), tenant: "other-tenant");
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Tier_administration_is_audited_even_when_allowed()
    {
        // Marked Sensitive: an allow is as interesting as a deny when the act reprices a whole network, and
        // "who moved this hospital into T1, and when" is the first question any cost review asks.
        ProviderPolicies.Rules()
            .Single(r => r.Action == ProviderPolicies.Actions.NetworkAdmin)
            .Sensitive.Should().BeTrue();
    }
}
