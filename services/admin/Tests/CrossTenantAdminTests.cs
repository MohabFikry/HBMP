using FluentAssertions;
using Mersal.Admin.Api;
using Mersal.Auth;

namespace Mersal.Admin.Tests;

/// <summary>
/// Phase 18.B2 (audit R2 S-series) — a body-supplied tenant on an admin write path.
///
/// Every admin write takes an optional <c>Tenant</c> in its request body. The old resolver handed a
/// non-global admin its OWN tenant back and carried on, so naming someone else's tenant produced a 201 for
/// an action that landed somewhere else entirely: an Org Admin's cross-tenant role grant became a grant in
/// their own tenant, against a subject id that means a different person there. The caller was told it
/// worked, and the audit trail recorded a grant nobody meant to make. Silence is the defect; the substituted
/// tenant is only how it manifests.
/// </summary>
public class CrossTenantAdminTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private static HbmpPrincipal Admin(string tenant, params string[] roles) => new()
    {
        Subject = "admin-under-test",
        Roles = new HashSet<string>(roles),
        Scopes = new HashSet<string> { "admin:read", "admin:write" },
        TenantId = tenant,
    };

    [Fact]
    public void An_org_admin_naming_another_tenant_is_refused_not_redirected()
    {
        var r = AdminContracts.ResolveTenantOrDeny(Admin(TenantA, "org_admin"), TenantB);

        r.IsAllowed.Should().BeFalse();
        r.ReasonCode.Should().Be("cross-tenant-denied");
        r.Tenant.Should().BeNull("substituting the caller's own tenant would apply the write somewhere it was never aimed");
    }

    [Fact]
    public void An_org_admin_naming_its_own_tenant_proceeds()
    {
        // The SPA sends the active tenant explicitly; that must stay a normal, non-privileged request.
        AdminContracts.ResolveTenantOrDeny(Admin(TenantA, "org_admin"), TenantA)
            .Should().BeEquivalentTo(TenantResolution.Allowed(TenantA));
    }

    [Fact]
    public void Omitting_the_tenant_falls_back_to_the_callers_own()
    {
        AdminContracts.ResolveTenantOrDeny(Admin(TenantA, "org_admin"), null).Tenant.Should().Be(TenantA);
        AdminContracts.ResolveTenantOrDeny(Admin(TenantA, "org_admin"), "  ").Tenant.Should().Be(TenantA);
    }

    [Fact]
    public void Only_the_global_super_admin_may_act_across_tenants()
    {
        AdminContracts.ResolveTenantOrDeny(Admin(TenantA, "super_admin"), TenantB).Tenant.Should().Be(TenantB);

        // Holding admin:write is not enough — Org Admin holds it too, which is exactly why the check is on
        // the role and not the scope.
        foreach (var role in new[] { "org_admin", "security_officer", "compliance_officer", "clinical_director" })
            AdminContracts.ResolveTenantOrDeny(Admin(TenantA, role), TenantB).ReasonCode
                .Should().Be("cross-tenant-denied", "{0} is a tenant-local role", role);
    }

    [Fact]
    public void A_principal_with_no_tenant_at_all_is_a_bad_request_not_a_denial()
    {
        // Distinct from cross-tenant: nothing was attempted against another tenant, the token is just unusable
        // here. A 403 would misdescribe it and send the caller looking for a permission they already have.
        var r = AdminContracts.ResolveTenantOrDeny(Admin(tenant: "", "org_admin"), null);
        r.ReasonCode.Should().Be("no-tenant");
    }

    [Fact]
    public void A_cross_tenant_refusal_is_a_403_and_a_missing_tenant_is_a_400()
    {
        StatusOf(TenantResolution.Denied("cross-tenant-denied")).Should().Be(403);
        StatusOf(TenantResolution.Denied("no-tenant")).Should().Be(400);
    }

    private static int StatusOf(TenantResolution r)
    {
        // IResult carries its status in a non-public property on the ProblemHttpResult it produces; reading it
        // via the public ProblemDetails surface keeps the assertion honest without a full host.
        var result = r.ToProblem();
        var problem = result.GetType().GetProperty("ProblemDetails")?.GetValue(result)
            as Microsoft.AspNetCore.Mvc.ProblemDetails;
        return problem?.Status ?? throw new InvalidOperationException("expected a ProblemDetails result");
    }
}
