using FluentAssertions;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Authz.Tests;

public class RowScopeTests
{
    private static HbmpPrincipal Principal(string? tenant, string? provider = null) => new()
    {
        Subject = "u", Roles = new HashSet<string>(), Scopes = new HashSet<string>(),
        TenantId = tenant, ProviderId = provider, MfaSatisfied = true,
    };

    [Fact]
    public void Provider_scope_limits_rows_to_own_provider()
    {
        var scope = RowScope.For(Principal("t0", provider: "prov-A"));

        scope.Allows(rowTenantId: "t0", rowProviderId: "prov-A").Should().BeTrue();
        scope.Allows(rowTenantId: "t0", rowProviderId: "prov-B").Should().BeFalse(); // cross-provider denied
    }

    [Fact]
    public void Tenant_scope_blocks_cross_tenant_rows()
    {
        var scope = RowScope.For(Principal("t0"));

        scope.Allows(rowTenantId: "t0").Should().BeTrue();
        scope.Allows(rowTenantId: "t1").Should().BeFalse();
    }

    [Fact]
    public void Treating_beneficiary_scope_limits_doctor_to_treated_patients()
    {
        var scope = RowScope.For(Principal("t0"),
            beneficiaryIds: new HashSet<string> { "MRS-M-1", "MRS-M-2" });

        scope.Allows("t0", rowBeneficiaryId: "MRS-M-1").Should().BeTrue();
        scope.Allows("t0", rowBeneficiaryId: "MRS-M-9").Should().BeFalse(); // not a treated patient
    }

    [Fact]
    public void Unrestricted_scope_sees_all_rows()
    {
        var scope = RowScope.For(Principal("t0"), unrestricted: true);
        scope.Allows(rowTenantId: "t1", rowProviderId: "prov-Z").Should().BeTrue();
    }
}
