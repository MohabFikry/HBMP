using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;

namespace Mersal.Authz.Tests;

/// <summary>
/// The FHIR-façade min-necessary parity proof (phase 13.1). The façade reuses the SAME engine + role/scope
/// vocabulary as native APIs, so a role that cannot read a class of data natively is absent from that resource's
/// FHIR rule and is default-denied. These tests lock the boundary — especially "Finance cannot reach
/// Condition/diagnosis via FHIR" (13.1 acceptance #2).
/// </summary>
public class InteropPoliciesTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(InteropPolicies.Bundle(), new AuditClient(_outbox, new AuditClientContext("interop-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1",
        Roles = new HashSet<string> { role },
        Scopes = new HashSet<string>(scopes),
        TenantId = "t0",
        MfaSatisfied = true,
    };

    private Task<AuthzDecision> Evaluate(HbmpPrincipal p, string action) =>
        Engine().EvaluateAsync(new AuthzRequest(p, action, new ResourceRef { Type = InteropPolicies.Resource, TenantId = "t0" }, "fhir-facade"));

    [Fact]
    public async Task Doctor_can_read_Patient()
    {
        var d = await Evaluate(Principal("doctor", InteropPolicies.ReadScope("Patient")), InteropPolicies.ReadAction("Patient"));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Doctor_can_read_Condition()
    {
        var d = await Evaluate(Principal("doctor", InteropPolicies.ReadScope("Condition")), InteropPolicies.ReadAction("Condition"));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Finance_cannot_read_Condition_via_fhir()
    {
        // Even if a token erroneously carried the SMART scope, the ROLE is the boundary: finance is absent from
        // the Condition rule → default-deny (role-not-permitted), and the deny is audited.
        var d = await Evaluate(Principal("finance", InteropPolicies.ReadScope("Condition")), InteropPolicies.ReadAction("Condition"));
        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Be("role-not-permitted");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task Finance_can_read_Coverage_but_not_Patient_demographics()
    {
        var coverage = await Evaluate(Principal("finance", InteropPolicies.ReadScope("Coverage")), InteropPolicies.ReadAction("Coverage"));
        coverage.IsAllowed.Should().BeTrue();

        var patient = await Evaluate(Principal("finance", InteropPolicies.ReadScope("Patient")), InteropPolicies.ReadAction("Patient"));
        patient.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Reception_cannot_read_diagnosis_or_results()
    {
        (await Evaluate(Principal("reception", InteropPolicies.ReadScope("Condition")), InteropPolicies.ReadAction("Condition"))).IsAllowed.Should().BeFalse();
        (await Evaluate(Principal("reception", InteropPolicies.ReadScope("DiagnosticReport")), InteropPolicies.ReadAction("DiagnosticReport"))).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Pharmacist_reads_meds_and_allergies_not_diagnosis()
    {
        (await Evaluate(Principal("pharmacist", InteropPolicies.ReadScope("MedicationRequest")), InteropPolicies.ReadAction("MedicationRequest"))).IsAllowed.Should().BeTrue();
        (await Evaluate(Principal("pharmacist", InteropPolicies.ReadScope("AllergyIntolerance")), InteropPolicies.ReadAction("AllergyIntolerance"))).IsAllowed.Should().BeTrue();
        (await Evaluate(Principal("pharmacist", InteropPolicies.ReadScope("Condition")), InteropPolicies.ReadAction("Condition"))).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_smart_scope_is_denied()
    {
        // A doctor without the SMART scope (and no native alias in the rule) is denied at the scope check.
        var d = await Evaluate(Principal("doctor"), InteropPolicies.ReadAction("Patient"));
        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Be("missing-scope");
    }

    [Fact]
    public async Task Only_prescriber_can_write_ServiceRequest_and_MedicationRequest()
    {
        (await Evaluate(Principal("doctor", InteropPolicies.WriteScope("ServiceRequest")), InteropPolicies.WriteAction("ServiceRequest"))).IsAllowed.Should().BeTrue();
        (await Evaluate(Principal("nurse", InteropPolicies.WriteScope("MedicationRequest")), InteropPolicies.WriteAction("MedicationRequest"))).IsAllowed.Should().BeFalse();
        (await Evaluate(Principal("pharmacist", InteropPolicies.WriteScope("ServiceRequest")), InteropPolicies.WriteAction("ServiceRequest"))).IsAllowed.Should().BeFalse();
    }
}
