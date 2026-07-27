using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.1 authorization proof — the separation the PAS scope split exists to create.
///
/// Before phase 19 `policy:write` meant everything policy-shaped, so the officer enrolling a member at a desk
/// and the administrator authoring the benefit product that member is enrolled onto held the same authority.
/// Activating a plan version decides what thousands of people are entitled to and is resolvable retroactively
/// forever; enrolling one member is not remotely the same act. These tests hold that boundary, against the
/// real engine over <see cref="PolicyPolicies"/>.
/// </summary>
public class PolicyAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(PolicyPolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("policy-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Res(string tenant = "t0") => new() { Type = PolicyPolicies.Resource, TenantId = tenant };

    [Theory]
    [InlineData("org_admin")]
    [InlineData("super_admin")]
    [InlineData("policy_admin")]
    public async Task An_administrator_may_author_benefit_configuration(string role)
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal(role, "policy:admin"), PolicyPolicies.Admin, Res()));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Member_administration_does_not_confer_the_authority_to_author_a_plan()
    {
        // THE point of the split. beneficiary_mgmt enrols members all day and must not be able to rewrite
        // what a member is entitled to — with either the role or the scope it legitimately holds.
        var withScope = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("beneficiary_mgmt", "policy:write", "policy:read"), PolicyPolicies.Admin, Res()));
        withScope.IsAllowed.Should().BeFalse();

        // And holding the admin SCOPE without the role is not enough either — the rule requires both.
        var withoutRole = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("beneficiary_mgmt", "policy:admin"), PolicyPolicies.Admin, Res()));
        withoutRole.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Authoring_requires_the_admin_scope_even_for_an_administrator_role()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "policy:read", "policy:write"), PolicyPolicies.Admin, Res()));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Beneficiary_management_may_administer_members()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("beneficiary_mgmt", "policy:write"), PolicyPolicies.Write, Res()));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_clinician_may_not_administer_members()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "policy:write"), PolicyPolicies.Write, Res()));
        d.IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("beneficiary_mgmt")]
    [InlineData("medical_approval")]
    [InlineData("finance")]
    public async Task Any_role_holding_the_read_scope_may_read_the_configuration(string role)
    {
        // Reading is deliberately wide: the benefit rules are the vocabulary every adjudicating service is
        // judged by, and a plan version carries no PHI. Minimum-necessary bites at member level, not here.
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal(role, "policy:read"), PolicyPolicies.Read, Res()));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Reading_the_configuration_still_requires_the_read_scope()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("finance"), PolicyPolicies.Read, Res()));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task The_supervisory_increment_is_not_included_in_member_administration()
    {
        // Cancelling another user's note and approving a retro-effective change are supervisory acts (38 §5.5).
        var officer = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("beneficiary_mgmt", "policy:write", "policy:supervise"), PolicyPolicies.Supervise, Res()));
        officer.IsAllowed.Should().BeFalse();

        var supervisor = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "policy:supervise"), PolicyPolicies.Supervise, Res()));
        supervisor.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Every_policy_action_is_tenant_scoped()
    {
        // A principal from another tenant is denied even holding the right role and scope.
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "policy:admin"), PolicyPolicies.Admin, Res(tenant: "other-tenant")));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Authoring_and_supervising_are_audited_even_when_allowed()
    {
        // Both are marked Sensitive: an allow is as interesting as a deny when the act changes what a
        // population is entitled to, or withdraws another person's signed note.
        PolicyPolicies.Rules().Single(r => r.Action == PolicyPolicies.Admin).Sensitive.Should().BeTrue();
        PolicyPolicies.Rules().Single(r => r.Action == PolicyPolicies.Supervise).Sensitive.Should().BeTrue();
    }
}
