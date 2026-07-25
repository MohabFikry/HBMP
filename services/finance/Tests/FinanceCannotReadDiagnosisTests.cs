using System.Reflection;
using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Finance.Domain;

namespace Mersal.Finance.Tests;

/// <summary>THE required invariant proof (phase 10.2, 11-permission-matrix hard-rule "Finance <c>diagnosis</c> =
/// ❌"). Three layers, one test class:
/// (a) NO finance projection type exposes any clinical/diagnosis field — the FinanceProjection guard rejects it;
/// (b) a Finance principal calling any clinical action (emr:read, emr:read-oversight, reporting clinical) is
///     DENIED (403) and the deny is audited — Finance holds no clinical rule;
/// (c) Finance MAY read its own zone (utilization / settlement / summary) — the control denies clinical, not finance.
/// </summary>
public class FinanceCannotReadDiagnosisTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(FinancePolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("finance-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Finance(params string[] scopes) => new()
    {
        Subject = "fin-1", Roles = new HashSet<string> { "finance" }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    // ---- (a) structural: no finance DTO can carry a clinical field --------------------------------------
    [Fact]
    public void No_finance_projection_type_exposes_a_clinical_field()
    {
        var projections = typeof(IFinanceProjection).Assembly.GetTypes()
            .Where(t => typeof(IFinanceProjection).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .ToList();

        projections.Should().NotBeEmpty("the finance surface is built from IFinanceProjection DTOs");
        foreach (var t in projections)
            FinanceProjection.Offenders(t).Should().BeEmpty($"{t.Name} must expose no clinical field (finance ≠ diagnosis)");
    }

    [Fact]
    public void The_projection_guard_rejects_a_type_that_adds_a_diagnosis_field()
    {
        // A hypothetical finance DTO that (wrongly) carried a diagnosis must be caught by the guard.
        var offenders = FinanceProjection.Offenders(typeof(LeakyProjection));
        offenders.Should().Contain(o => o.Contains("Diagnosis"));
        var act = () => FinanceProjection.Guard(typeof(LeakyProjection));
        act.Should().Throw<InvalidOperationException>().WithMessage("*clinical*");
    }

    private sealed record LeakyProjection(string ServiceCode, string DiagnosisCode) : IFinanceProjection;

    // ---- (b) authorization: Finance is denied every clinical action ------------------------------------
    [Theory]
    [InlineData("emr:read", "diagnosis")]
    [InlineData("emr:read", "encounter")]
    [InlineData("emr:read-oversight", "diagnosis")]
    public async Task Finance_calling_a_clinical_action_is_denied_and_audited(string action, string resourceType)
    {
        var res = new ResourceRef { Type = resourceType, TenantId = "t0" };
        var d = await Engine().EvaluateAsync(new AuthzRequest(Finance("emr:read", "finance:read"), action, res));
        d.IsAllowed.Should().BeFalse();     // finance holds no clinical rule → default-denied
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    // ---- (c) Finance MAY read its own zone -------------------------------------------------------------
    [Fact]
    public async Task Finance_may_read_utilization_settlement_and_summary()
    {
        var res = new ResourceRef { Type = FinancePolicies.Resource, TenantId = "t0" };
        foreach (var action in new[] { FinancePolicies.ReadUtilization, FinancePolicies.ReadSettlement, FinancePolicies.ReadSummary })
        {
            var d = await Engine().EvaluateAsync(new AuthzRequest(Finance("finance:read"), action, res));
            d.IsAllowed.Should().BeTrue($"finance may read its own zone action {action}");
        }
    }

    // ---- the read-model schema also carries no clinical column -----------------------------------------
    [Fact]
    public void Utilization_and_settlement_facts_carry_no_diagnosis_or_clinical_field()
    {
        var forbidden = new[] { "diagnosis", "icd", "clinical", "note", "result", "symptom", "allergy" };
        foreach (var t in new[] { typeof(UtilizationFact), typeof(SettlementLine), typeof(Settlement) })
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant());
            props.Should().NotContain(p => forbidden.Any(p.Contains));
        }
    }
}
