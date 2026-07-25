using System.Reflection;
using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Reporting.Domain;

namespace Mersal.Reporting.Tests;

/// <summary>Authorization proof for the reporting surface (US-073, 11-permission-matrix finance ≠ diagnosis): the
/// finance role may read the financial summary but is DEFAULT-DENIED the clinical-coded top-diagnoses report; the
/// Medical Director may read both operational and clinical zones; the projection seam needs the project scope; and
/// — the schema invariant — the financial fact carries NO diagnosis/clinical field. Exercised against the real
/// engine over <see cref="ReportingPolicies"/>.</summary>
public class ReportingAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(ReportingPolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("reporting-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Res() => new() { Type = ReportingPolicies.Resource, TenantId = "t0" };

    [Fact]
    public async Task Finance_may_read_the_financial_summary()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("finance", "reporting:read-financial"), ReportingPolicies.ReadFinancial, Res()));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Finance_is_denied_the_clinical_coded_top_diagnoses_report()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("finance", "reporting:read", "reporting:read-financial"), ReportingPolicies.ReadClinical, Res()));
        d.IsAllowed.Should().BeFalse();     // finance ≠ diagnosis
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    [Fact]
    public async Task Medical_director_may_read_operational_and_clinical_zones()
    {
        var op = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "reporting:read"), ReportingPolicies.ReadOperational, Res()));
        var cl = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "reporting:read"), ReportingPolicies.ReadClinical, Res()));
        op.IsAllowed.Should().BeTrue();
        cl.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Projection_seam_requires_the_project_scope()
    {
        var ok = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("system", "reporting:project"), ReportingPolicies.Project, Res()));
        var no = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("manager", "reporting:read"), ReportingPolicies.Project, Res()));
        ok.IsAllowed.Should().BeTrue();
        no.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Financial_fact_carries_no_diagnosis_or_clinical_field()
    {
        // The finance zone must never expose diagnoses (finance ≠ diagnosis). Enforced at the type/schema level.
        var forbidden = new[] { "diagnosis", "icd", "clinical", "note", "result" };
        var props = typeof(FinancialFact).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant());
        props.Should().NotContain(p => forbidden.Any(p.Contains));
    }
}
