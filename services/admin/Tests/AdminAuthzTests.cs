using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Admin.Tests;

/// <summary>Authorization proof for the admin surface over the real engine + <see cref="AdminPolicies"/>: Org Admin
/// and Super Admin may administer access; a non-admin role is default-denied; only Super Admin may propose a policy
/// bundle; and every admin allow is audited (who granted / who viewed the matrix). Super Admin acts cross-tenant
/// (resource tenant null) while Org Admin is pinned to its own tenant.</summary>
public class AdminAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(AdminPolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("admin-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, string tenant, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = tenant, MfaSatisfied = true,
    };

    private static ResourceRef Res(string? tenant) => new() { Type = AdminPolicies.Resource, TenantId = tenant };

    [Fact]
    public async Task Org_admin_may_grant_a_role_in_its_own_tenant()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "t0", "admin:write"), AdminPolicies.GrantRole, Res("t0")));
        d.IsAllowed.Should().BeTrue();
        _outbox.Events.Should().Contain(e => e.EntityType == "admin"); // sensitive allow is audited
    }

    [Fact]
    public async Task Org_admin_is_denied_cross_tenant()
    {
        // Org Admin bound to t0 cannot act on t1 (TenantMatch fails).
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "t0", "admin:write"), AdminPolicies.GrantRole, Res("t1")));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Super_admin_may_act_cross_tenant_global_scope()
    {
        // The gate sets the resource tenant to null for Super Admin → TenantMatch passes globally.
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("super_admin", "t0", "admin:write"), AdminPolicies.GrantRole, Res(null)));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_non_admin_role_is_default_denied_the_admin_surface()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "t0", "admin:write"), AdminPolicies.GrantRole, Res("t0")));
        d.IsAllowed.Should().BeFalse();
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    [Fact]
    public async Task Only_super_admin_may_propose_a_policy_bundle()
    {
        var org = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "t0", "admin:write"), AdminPolicies.ProposePolicy, Res("t0")));
        var sup = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("super_admin", "t0", "admin:write"), AdminPolicies.ProposePolicy, Res(null)));
        org.IsAllowed.Should().BeFalse();
        sup.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Master_data_edit_is_governance_only_org_admin_is_denied()
    {
        // FR-MDM-008: only clinical governance (Medical Director) + Super Admin may edit master data. Org Admin
        // manages access, not clinical reference content.
        var gov = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "t0", "admin:write"), AdminPolicies.EditMasterData, Res("t0")));
        var org = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "t0", "admin:write"), AdminPolicies.EditMasterData, Res("t0")));
        gov.IsAllowed.Should().BeTrue();
        org.IsAllowed.Should().BeFalse();
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    [Fact]
    public async Task Tenant_administration_is_super_admin_only()
    {
        var sup = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("super_admin", "t0", "admin:write"), AdminPolicies.ManageTenant, Res(null)));
        var org = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "t0", "admin:write"), AdminPolicies.ManageTenant, Res("t0")));
        sup.IsAllowed.Should().BeTrue();
        org.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Break_glass_request_and_approval_are_distinct_authorized_tiers()
    {
        // A doctor may REQUEST break-glass but not APPROVE it; a medical director may do both.
        var docReq = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "t0", "admin:break-glass"), AdminPolicies.BreakGlassRequest, Res("t0")));
        var docApprove = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "t0", "admin:break-glass"), AdminPolicies.BreakGlassApprove, Res("t0")));
        var dirApprove = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "t0", "admin:break-glass"), AdminPolicies.BreakGlassApprove, Res("t0")));
        docReq.IsAllowed.Should().BeTrue();
        docApprove.IsAllowed.Should().BeFalse();
        dirApprove.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Reading_the_access_matrix_is_a_sensitive_audited_allow()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("org_admin", "t0", "admin:read"), AdminPolicies.ReadAccess, Res("t0")));
        d.IsAllowed.Should().BeTrue();
        _outbox.Events.Should().Contain(e => e.EntityType == "admin"); // the read itself is audited
    }
}
