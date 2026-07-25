using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Emr.Tests;

/// <summary>Authorization proof for the EMR clinical overlay (US-030, phase-4 guardrail — "treating-relationship
/// is enforced and tested … a non-treating clinician is denied (403) and audited"). Exercises the real
/// <see cref="DefaultAuthorizationEngine"/> over <see cref="EmrPolicies"/>: a treating doctor is allowed; a
/// non-treating doctor is denied and audited; reception/lab are denied clinical entirely; the approval team may
/// read but not write.</summary>
public class ClinicalAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(EmrPolicies.Bundle(), new AuditClient(_outbox, new AuditClientContext("emr-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Resource(string type, bool treating) => new()
    {
        Type = type, Id = "r-1", TenantId = "t0", BeneficiaryId = "BEN-1",
        TreatingBeneficiaryIds = treating ? new HashSet<string> { "BEN-1" } : new HashSet<string>(),
    };

    [Theory]
    [InlineData(EmrPolicies.Resources.Note)]
    [InlineData(EmrPolicies.Resources.Diagnosis)]
    [InlineData(EmrPolicies.Resources.Vital)]
    public async Task Treating_doctor_may_write_clinical(string resource)
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "emr:write"), "emr:write", Resource(resource, treating: true)));
        d.IsAllowed.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.TreatingRelationship);
    }

    [Fact]
    public async Task Non_treating_doctor_is_denied_and_audited()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "emr:write"), "emr:write", Resource(EmrPolicies.Resources.Diagnosis, treating: false)));
        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Contain("treating-relationship");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task Reception_cannot_read_clinical_notes()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("reception", "emr:read"), "emr:read", Resource(EmrPolicies.Resources.Note, treating: true)));
        d.IsAllowed.Should().BeFalse();   // no rule maps reception → clinical: default-deny
    }

    [Fact]
    public async Task Lab_tech_cannot_read_diagnosis()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("lab_tech", "emr:read"), "emr:read", Resource(EmrPolicies.Resources.Diagnosis, treating: true)));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Approval_team_may_read_clinical_without_treating_relationship()
    {
        // The gate routes approval-team reads to the oversight action (no treating relationship required).
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_approval", "emr:read"), EmrPolicies.ReadOversight, Resource(EmrPolicies.Resources.Note, treating: false)));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Approval_team_may_not_write_clinical()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_approval", "emr:write"), "emr:write", Resource(EmrPolicies.Resources.Note, treating: false)));
        d.IsAllowed.Should().BeFalse();   // approval team is read-only on EMR
    }
}
