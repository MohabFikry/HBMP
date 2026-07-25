using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Orders.Tests;

/// <summary>Authorization proof for order creation (US-032 / phase-4 guardrail): a treating doctor may create an
/// order; a non-treating doctor is denied and audited; a lab tech cannot create orders (no rule → default-deny).</summary>
public class OrderAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(OrdersPolicies.Bundle(), new AuditClient(_outbox, new AuditClientContext("orders-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Resource(bool treating) => new()
    {
        Type = "investigation_order", TenantId = "t0", BeneficiaryId = "BEN-1",
        TreatingBeneficiaryIds = treating ? new HashSet<string> { "BEN-1" } : new HashSet<string>(),
    };

    [Fact]
    public async Task Treating_doctor_may_create_order()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "orders:write"), OrdersPolicies.Create, Resource(treating: true)));
        d.IsAllowed.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.TreatingRelationship);
    }

    [Fact]
    public async Task Non_treating_doctor_is_denied_and_audited()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "orders:write"), OrdersPolicies.Create, Resource(treating: false)));
        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Contain("treating-relationship");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task Lab_tech_cannot_create_order()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("lab_tech", "orders:write"), OrdersPolicies.Create, Resource(treating: true)));
        d.IsAllowed.Should().BeFalse();   // no create rule for lab_tech → default-deny
    }
}
