using System.Reflection;
using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Claims.Api;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>THE required invariant proof (Phase 10b, 11-permission-matrix hard rule "Finance/Claims → diagnosis =
/// denied"). Three layers, one class:
/// (a) NO claims entity or projection DTO carries any clinical field name — the schema is codes + amounts only;
/// (b) a Claims principal calling any clinical action (emr read, diagnosis) is DENIED (403) and the deny is audited;
/// (c) a Claims Officer MAY read its own zone (claims:read/review/decide) — the control denies clinical, not claims.</summary>
public class ClaimsCannotReadDiagnosisTests
{
    private static readonly string[] Forbidden =
        ["diagnosis", "icd", "clinical", "emrnote", "note", "result", "symptom", "allergy", "soap", "vital"];

    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(ClaimsPolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("claims-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Claims(string role, params string[] scopes) => new()
    {
        Subject = "clm-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    // ---- (a) structural: no claims type exposes a clinical field ----------------------------------------
    [Fact]
    public void No_claims_entity_or_projection_exposes_a_clinical_field()
    {
        var types = new[]
        {
            typeof(Claim), typeof(ClaimLine), typeof(ClaimIntakeEvent),
            typeof(ClaimView), typeof(ClaimLineView), typeof(ClaimIntakeRequest),
        };
        foreach (var t in types)
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant());
            props.Should().NotContain(p => Forbidden.Any(p.Contains), $"{t.Name} must carry no clinical field (claims ≠ diagnosis)");
        }
    }

    // ---- (b) authorization: Claims is denied every clinical action -------------------------------------
    [Theory]
    [InlineData("emr:read", "diagnosis")]
    [InlineData("emr:read", "encounter")]
    [InlineData("emr:read-oversight", "diagnosis")]
    public async Task Claims_officer_calling_a_clinical_action_is_denied_and_audited(string action, string resourceType)
    {
        var res = new ResourceRef { Type = resourceType, TenantId = "t0" };
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Claims("claims_officer", "claims:read", "claims:review", "emr:read"), action, res));
        d.IsAllowed.Should().BeFalse("claims roles hold no clinical rule → default-denied");
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    // ---- (c) a Claims Officer MAY act within its own zone ----------------------------------------------
    [Fact]
    public async Task Claims_officer_may_read_and_review_and_decide_claims()
    {
        var res = new ResourceRef { Type = ClaimsPolicies.Resource, TenantId = "t0" };
        var p = Claims("claims_officer", "claims:read", "claims:review", "claims:decide");
        foreach (var action in new[] { ClaimsPolicies.ReadClaim, ClaimsPolicies.Review, ClaimsPolicies.Decide })
        {
            var d = await Engine().EvaluateAsync(new AuthzRequest(p, action, res));
            d.IsAllowed.Should().BeTrue($"a claims officer may perform its own zone action {action}");
        }
    }
}
