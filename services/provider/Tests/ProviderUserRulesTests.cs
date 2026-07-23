using FluentAssertions;
using Mersal.Provider.Domain;

namespace Mersal.Provider.Tests;

public class ProviderUserRulesTests
{
    [Fact]
    public void NetworkTeam_may_provision_admin_and_tech_roles()
    {
        ProviderUserRules.CanProvision(["network_team"], "provider_admin").Allowed.Should().BeTrue();
        ProviderUserRules.CanProvision(["network_team"], "lab_tech").Allowed.Should().BeTrue();
        ProviderUserRules.CanProvision(["network_team"], "pharmacist").Allowed.Should().BeTrue();
    }

    [Fact]
    public void ProviderAdmin_may_provision_techs_but_not_another_admin()
    {
        ProviderUserRules.CanProvision(["provider_admin"], "lab_tech").Allowed.Should().BeTrue();
        ProviderUserRules.CanProvision(["provider_admin"], "provider_admin").Allowed.Should().BeFalse();  // no self-elevation
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("nurse")]
    [InlineData("medical_director")]
    public void Clinical_roles_are_never_provisioned_through_onboarding(string role)
    {
        ProviderUserRules.CanProvision(["network_team"], role).Allowed.Should().BeFalse();
        ProviderUserRules.CanProvision(["provider_admin"], role).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Unknown_provider_role_is_rejected()
        => ProviderUserRules.CanProvision(["network_team"], "provider_finance").Allowed.Should().BeFalse();

    [Fact]
    public void A_random_role_holder_cannot_provision()
        => ProviderUserRules.CanProvision(["reception"], "lab_tech").Allowed.Should().BeFalse();
}
