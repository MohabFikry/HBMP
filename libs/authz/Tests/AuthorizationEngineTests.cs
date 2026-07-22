using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Authz.Tests;

public class AuthorizationEngineTests
{
    private readonly InMemoryAuditOutbox _outbox = new();
    private AuditClient Audit() => new(_outbox, new AuditClientContext("test"), TimeProvider.System);

    private static HbmpPrincipal Principal(string role, string? tenant = "t0", string? provider = null,
        params string[] scopes) => new()
    {
        Subject = "u-1",
        Roles = new HashSet<string> { role },
        Scopes = new HashSet<string>(scopes),
        TenantId = tenant,
        ProviderId = provider,
        MfaSatisfied = true,
    };

    private DefaultAuthorizationEngine Engine(IBreakGlassProvider? bg = null) =>
        new(DefaultPolicies.Bundle(), Audit(), bg ?? NullBreakGlassProvider.Instance, TimeProvider.System);

    [Fact]
    public async Task Default_deny_for_unmapped_action()
    {
        var req = new AuthzRequest(Principal("doctor"), "totally:unknown",
            new ResourceRef { Type = "whatever" });

        var d = await Engine().EvaluateAsync(req);

        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Be("no-matching-rule");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task Treating_doctor_allowed_on_assigned_patient()
    {
        var req = new AuthzRequest(Principal("doctor"), "emr:read", new ResourceRef
        {
            Type = "encounter", Id = "ENC-1", TenantId = "t0",
            BeneficiaryId = "MRS-M-1",
            TreatingBeneficiaryIds = new HashSet<string> { "MRS-M-1" },
        });

        var d = await Engine().EvaluateAsync(req);

        d.IsAllowed.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.TreatingRelationship);
    }

    [Fact]
    public async Task Non_treating_doctor_denied_and_audited()
    {
        var req = new AuthzRequest(Principal("doctor"), "emr:read", new ResourceRef
        {
            Type = "encounter", Id = "ENC-9", TenantId = "t0",
            BeneficiaryId = "MRS-M-2",
            TreatingBeneficiaryIds = new HashSet<string>(), // not treating
        });

        var d = await Engine().EvaluateAsync(req);

        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Contain("treating-relationship");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task Lab_cross_provider_read_denied_provider_ownership()
    {
        var req = new AuthzRequest(Principal("lab_tech", provider: "prov-A", scopes: "orders:read"),
            "orders:read", new ResourceRef
            {
                Type = "order_line", Id = "ORD-1:1", TenantId = "t0", ProviderId = "prov-B", // another provider
            });

        var d = await Engine().EvaluateAsync(req);

        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Contain("provider-ownership");
    }

    [Fact]
    public async Task Missing_scope_denied()
    {
        var req = new AuthzRequest(Principal("lab_tech", provider: "prov-A"), // no orders:read scope
            "orders:read", new ResourceRef { Type = "order_line", TenantId = "t0", ProviderId = "prov-A" });

        (await Engine().EvaluateAsync(req)).ReasonCode.Should().Be("missing-scope");
    }

    [Fact]
    public async Task Break_glass_widens_denied_access_and_audits_high_severity()
    {
        var now = DateTimeOffset.UtcNow;
        var grant = new BreakGlassGrant
        {
            GrantId = "bg-1", SubjectUserId = "u-1", ApprovedByUserId = "reviewer-2",
            NotBefore = now.AddMinutes(-1), ExpiresAt = now.AddMinutes(30),
            ScopedResourceIds = new HashSet<string> { "ENC-9" },
        };
        var bg = new StubBreakGlass(grant);

        var req = new AuthzRequest(Principal("doctor"), "emr:read", new ResourceRef
        {
            Type = "encounter", Id = "ENC-9", TenantId = "t0",
            BeneficiaryId = "MRS-M-2", TreatingBeneficiaryIds = new HashSet<string>(), // not treating
        });

        var d = await Engine(bg).EvaluateAsync(req);

        d.IsAllowed.Should().BeTrue();
        d.BreakGlass.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.BreakGlass);
        _outbox.Events.Should().ContainSingle()
            .Which.Should().Match<AuditEvent>(e => e.BreakGlass && e.Severity == AuditSeverity.High);
    }

    [Fact]
    public async Task Expired_break_glass_does_not_widen()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = new BreakGlassGrant
        {
            GrantId = "bg-2", SubjectUserId = "u-1", ApprovedByUserId = "reviewer-2",
            NotBefore = now.AddHours(-2), ExpiresAt = now.AddHours(-1), // already expired
            ScopedResourceIds = new HashSet<string> { "ENC-9" },
        };

        var req = new AuthzRequest(Principal("doctor"), "emr:read", new ResourceRef
        {
            Type = "encounter", Id = "ENC-9", TenantId = "t0",
            BeneficiaryId = "MRS-M-2", TreatingBeneficiaryIds = new HashSet<string>(),
        });

        (await Engine(new StubBreakGlass(expired)).EvaluateAsync(req)).IsAllowed.Should().BeFalse();
    }

    private sealed class StubBreakGlass(BreakGlassGrant grant) : IBreakGlassProvider
    {
        public BreakGlassGrant? ActiveGrantFor(HbmpRequestContext ctx) => grant;
    }
}
