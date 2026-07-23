using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Provider.Tests;

/// <summary>ABAC layer of provider isolation (2b.3), proven INDEPENDENTLY of RLS (see RlsIsolationTests).
/// Uses the reusable <see cref="ProviderPolicies"/> bundle through the real authorization engine, so a
/// green test here is the same deny orders/pharmacy get when they import the bundle.</summary>
public class ProviderIsolationTests
{
    private readonly InMemoryAuditOutbox _outbox = new();
    private DefaultAuthorizationEngine Engine() =>
        new(ProviderPolicies.Bundle(), new AuditClient(_outbox, new AuditClientContext("test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, string? tenant = "t0", string? provider = null, params string[] scopes)
        => new() { Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes), TenantId = tenant, ProviderId = provider, MfaSatisfied = true };

    private static AuthzRequest Read(HbmpPrincipal p, string action, string tenant, string providerId)
        => new(p, action, new ResourceRef { Type = "provider", Id = providerId, TenantId = tenant, ProviderId = providerId });

    [Fact]
    public async Task ProviderAdmin_denied_reading_another_provider_and_audited()
    {
        var a = Principal("provider_admin", provider: "prov-A", scopes: "provider:read");
        var d = await Engine().EvaluateAsync(Read(a, ProviderPolicies.Actions.ReadOwn, "t0", "prov-B"));

        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Contain("provider-ownership");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task ProviderAdmin_allowed_on_own_provider()
    {
        var a = Principal("provider_admin", provider: "prov-A", scopes: "provider:read");
        var d = await Engine().EvaluateAsync(Read(a, ProviderPolicies.Actions.ReadOwn, "t0", "prov-A"));

        d.IsAllowed.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.ProviderOwnership);
    }

    [Fact]
    public async Task NetworkTeam_manages_any_provider_in_its_tenant()
    {
        var nt = Principal("network_team", tenant: "t0", scopes: "provider:read");
        var d = await Engine().EvaluateAsync(Read(nt, ProviderPolicies.Actions.Read, "t0", "prov-Z"));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Cross_tenant_read_denied()
    {
        // A Network Team member of tenant t1 cannot read a provider owned by tenant t0.
        var nt = Principal("network_team", tenant: "t1", scopes: "provider:read");
        var d = await Engine().EvaluateAsync(Read(nt, ProviderPolicies.Actions.Read, "t0", "prov-Z"));

        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Contain("tenant-match");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task Reuse_a_downstream_service_importing_the_bundle_gets_the_same_deny()
    {
        // Phase 5/6 import ProviderPolicies.Bundle() and gate their queue on provider-queue:read. A lab tech
        // of provider A must be denied provider B's queue by exactly the same PO rule.
        var lab = Principal("lab_tech", provider: "prov-A", scopes: "provider:read");
        var foreign = new AuthzRequest(lab, ProviderPolicies.Actions.QueueRead,
            new ResourceRef { Type = "provider_queue", Id = "q-B", TenantId = "t0", ProviderId = "prov-B" });
        var own = new AuthzRequest(lab, ProviderPolicies.Actions.QueueRead,
            new ResourceRef { Type = "provider_queue", Id = "q-A", TenantId = "t0", ProviderId = "prov-A" });

        (await Engine().EvaluateAsync(foreign)).IsAllowed.Should().BeFalse();
        (await Engine().EvaluateAsync(own)).IsAllowed.Should().BeTrue();
    }
}
