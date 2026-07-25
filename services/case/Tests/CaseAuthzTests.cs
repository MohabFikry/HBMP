using System.Reflection;
using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Case.Domain;

namespace Mersal.Case.Tests;

/// <summary>Authorization proof for the case surface (10 §3.11 — access follows assignment). The distinctive
/// control is the case-assignment ABAC condition: a Case Manager reaches a case (and its coordination-360) ONLY
/// while they hold an ACTIVE assignment; unassignment (empty set) revokes it → 403 (audited). Supervisory roles
/// reach a case for oversight without an assignment but CANNOT self-assign. The 360 DTO is proven structurally
/// incapable of carrying a raw clinical note / result. Exercised against the real engine over
/// <see cref="CasePolicies"/>.</summary>
public class CaseAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(CasePolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("case-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Case(string caseId, params string[] assigned) => new()
    {
        Type = CasePolicies.Resource, Id = caseId, TenantId = "t0",
        AssignedCaseIds = new HashSet<string>(assigned, StringComparer.Ordinal),
    };

    [Fact]
    public async Task Case_manager_with_an_active_assignment_may_read_the_case()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("case_manager", "case:read"), CasePolicies.Read, Case("CASE-1", "CASE-1")));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Case_manager_without_an_assignment_is_denied_and_audited()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("case_manager", "case:read"), CasePolicies.Read, Case("CASE-1" /* no assignment */)));
        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Contain("case-assignment");
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    [Fact]
    public async Task Unassignment_revokes_access_to_the_case_and_the_360()
    {
        // After unassignment the caller's active-assignment set no longer contains the case → both read and the
        // 360 assembly are denied (10 §3.11 "unassignment revokes it").
        var read = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("case_manager", "case:read"), CasePolicies.Read, Case("CASE-9" /* revoked */)));
        var threesixty = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("case_manager", "case:read"), CasePolicies.Read360, Case("CASE-9")));
        read.IsAllowed.Should().BeFalse();
        threesixty.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Case_manager_with_assignment_may_assemble_the_coordination_360()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("case_manager", "case:read"), CasePolicies.Read360, Case("CASE-2", "CASE-2")));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Supervisor_reads_a_case_for_oversight_without_an_assignment()
    {
        // The gate maps a supervisor's plain read to the oversight action; here we assert the rule directly.
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("manager", "case:read"), CasePolicies.ReadOversight, Case("CASE-3" /* none */)));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Case_manager_cannot_assign_only_a_supervisor_can()
    {
        var cm = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("case_manager", "case:manage"), CasePolicies.Manage, Case("CASE-4", "CASE-4")));
        var sup = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("manager", "case:manage"), CasePolicies.Manage, Case("CASE-4")));
        cm.IsAllowed.Should().BeFalse();   // no self-grant of the access anchor
        sup.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Coordination_360_dto_cannot_carry_a_raw_clinical_note_or_result()
    {
        // The clinical portion is a SUMMARY: diagnosis coord-visible; notes/rx/results are masked counts only. No
        // property on the DTO graph may name a raw note / prescription / result body.
        var forbidden = new[] { "notebody", "notetext", "resultvalue", "prescriptiontext", "rawnote", "labresult" };
        var types = new[]
        {
            typeof(Beneficiary360), typeof(ClinicalSummary), typeof(MaskedSection),
            typeof(CoverageSummary), typeof(CarePlanSummary), typeof(ApprovalSummary),
        };
        foreach (var t in types)
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant());
            props.Should().NotContain(p => forbidden.Contains(p));
        }
    }
}
