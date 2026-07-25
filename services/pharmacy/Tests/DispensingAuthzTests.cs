using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Pharmacy.Tests;

/// <summary>Authorization proof for the phase-6 dispensing surface (US-050/US-051 guardrails, min-necessary): a
/// pharmacist may read the dispensable queue and dispense a line for their OWN pharmacy (provider-ownership); a
/// pharmacist cannot reach another pharmacy's scope; a doctor cannot dispense; and — proving pharmacies ≠
/// investigation results — the pharmacy policy bundle grants a pharmacist NO order-result read at all. Exercised
/// against the real engine over <see cref="PharmacyPolicies"/>.</summary>
public class DispensingAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(PharmacyPolicies.Bundle(), new AuditClient(_outbox, new AuditClientContext("pharmacy-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, string? providerId, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", ProviderId = providerId, MfaSatisfied = true,
    };

    [Fact]
    public async Task Pharmacist_may_read_its_own_dispensable_queue()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("pharmacist", "pharm-A", "provider:read"), ProviderPolicies.Actions.QueueRead,
            new ResourceRef { Type = "provider_queue", TenantId = "t0", ProviderId = "pharm-A" }));
        d.IsAllowed.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.ProviderOwnership);
    }

    [Fact]
    public async Task Pharmacist_cannot_read_another_pharmacys_queue()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("pharmacist", "pharm-A", "provider:read"), ProviderPolicies.Actions.QueueRead,
            new ResourceRef { Type = "provider_queue", TenantId = "t0", ProviderId = "pharm-B" }));
        d.IsAllowed.Should().BeFalse();
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    [Fact]
    public async Task Pharmacist_may_dispense_a_line_for_its_own_pharmacy()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("pharmacist", "pharm-A", "pharmacy:dispense"), PharmacyPolicies.Dispense,
            new ResourceRef { Type = "prescription_line", TenantId = "t0", ProviderId = "pharm-A" }));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Doctor_cannot_dispense_a_line()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "pharm-A", "pharmacy:dispense"), PharmacyPolicies.Dispense,
            new ResourceRef { Type = "prescription_line", TenantId = "t0", ProviderId = "pharm-A" }));
        d.IsAllowed.Should().BeFalse();   // no dispense rule for doctor → default-deny
    }

    [Fact]
    public async Task Pharmacy_bundle_grants_a_pharmacist_no_investigation_result_read()
    {
        // Min-necessary: a pharmacist has no business seeing lab/imaging results. The pharmacy bundle carries no
        // order-result rule at all, so evaluating the orders result-read action here is default-denied.
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("pharmacist", "pharm-A", "orders:read"), OrdersPolicies.ReadResult,
            new ResourceRef { Type = "order_result", TenantId = "t0" }));
        d.IsAllowed.Should().BeFalse();
    }
}
