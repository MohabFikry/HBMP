using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Orders.Tests;

/// <summary>Authorization proof for the phase-5 fulfillment surface (US-040/US-041 guardrails, min-necessary):
/// a provider tech may read/consume ONLY their own provider's work (provider-ownership); a tech cannot reach
/// another facility's queue; a doctor cannot consume; and results are readable by the approval team but not by
/// an unrelated role. Exercised against the real engine over <see cref="OrdersPolicies"/>.</summary>
public class FulfillmentAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(OrdersPolicies.Bundle(), new AuditClient(_outbox, new AuditClientContext("orders-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, string? providerId, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", ProviderId = providerId, MfaSatisfied = true,
    };

    [Fact]
    public async Task Lab_tech_may_read_its_own_provider_queue()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("lab_tech", "prov-A", "provider:read"), ProviderPolicies.Actions.QueueRead,
            new ResourceRef { Type = "provider_queue", TenantId = "t0", ProviderId = "prov-A" }));
        d.IsAllowed.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.ProviderOwnership);
    }

    [Fact]
    public async Task Lab_tech_cannot_read_another_facilitys_queue()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("lab_tech", "prov-A", "provider:read"), ProviderPolicies.Actions.QueueRead,
            new ResourceRef { Type = "provider_queue", TenantId = "t0", ProviderId = "prov-B" }));
        d.IsAllowed.Should().BeFalse();
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    [Fact]
    public async Task Lab_tech_may_consume_a_line_for_its_own_provider()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("lab_tech", "prov-A", "orders:consume"), OrdersPolicies.Consume,
            new ResourceRef { Type = "order_line", TenantId = "t0", ProviderId = "prov-A" }));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Doctor_cannot_consume_a_line()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "prov-A", "orders:consume"), OrdersPolicies.Consume,
            new ResourceRef { Type = "order_line", TenantId = "t0", ProviderId = "prov-A" }));
        d.IsAllowed.Should().BeFalse();   // no consume rule for doctor → default-deny
    }

    [Fact]
    public async Task Approval_team_may_read_a_result_but_an_unrelated_role_cannot()
    {
        var allowed = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("approvals_team", null, "orders:read"), OrdersPolicies.ReadResult,
            new ResourceRef { Type = "order_result", TenantId = "t0" }));
        allowed.IsAllowed.Should().BeTrue();

        var denied = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("reception", null, "orders:read"), OrdersPolicies.ReadResult,
            new ResourceRef { Type = "order_result", TenantId = "t0" }));
        denied.IsAllowed.Should().BeFalse();
    }
}
