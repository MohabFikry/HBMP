using FluentAssertions;

namespace Mersal.Authz.Tests;

/// <summary>
/// The set of roles that cannot work without a provider, derived from the rules rather than listed.
/// </summary>
/// <remarks>
/// <para>A member of one of these roles whose membership carries no <c>provider_id</c> authenticates
/// perfectly, receives every scope their role grants, and is then refused every screen in their own portal —
/// each provider-scoped gate rejects a caller with no provider before any rule is evaluated. It happened to
/// <c>pharmacist</c>, <c>lab_tech</c> and <c>imaging_tech</c> at the same time and nothing said so; the 403
/// names a permissions problem, so the real cause is the last thing anyone checks.</para>
///
/// <para>These tests pin the DERIVATION, not a list. The point of computing the set from the policy rules is
/// that the next provider-scoped rule is covered the day it is written — so what has to hold is that the
/// computation finds the rules that exist, not that it produces one particular answer today.</para>
/// </remarks>
public class ProviderScopedRolesTests
{
    [Fact]
    public void The_set_is_discovered_from_the_rules_and_is_not_empty()
    {
        // Empty would mean the reflection walk silently stopped finding rule factories — which is the one
        // failure mode that turns this guard into a permanent all-clear.
        ProviderScopedRoles.All.Should().NotBeEmpty(
            "the roles are read from the compiled policy rules; an empty set means the walk broke, not that "
            + "no role is provider-scoped");
    }

    [Theory]
    [InlineData("pharmacist")]
    [InlineData("lab_tech")]
    [InlineData("imaging_tech")]
    public void The_three_roles_that_were_silently_unusable_are_covered(string role)
    {
        // Not a restatement of the list: each of these appears on a provider-ownership rule, so a change that
        // stopped the derivation seeing that rule would show up here as the specific role it stopped seeing.
        ProviderScopedRoles.Requires(role).Should().BeTrue();
    }

    [Fact]
    public void Every_discovered_role_really_does_appear_on_a_provider_ownership_rule()
    {
        // The converse direction. A set that over-reports would have identity-service warn about accounts
        // that are fine, and a guard that cries wolf is turned off.
        var onProviderRules = new[]
            {
                ProviderPolicies.Rules(), OrdersPolicies.Rules(), PharmacyPolicies.Rules(),
                ClaimsPolicies.Rules(),
                // `Bundle()`, not `Rules()` — DefaultPolicies is the odd one out, and it is where the
                // lab_tech/imaging_tech order_line rule lives.
                DefaultPolicies.Bundle().Rules,
            }
            .SelectMany(rules => rules)
            .Where(r => r.RequiredConditions.Contains(AbacConditions.ProviderOwnership))
            .SelectMany(r => r.Roles)
            .ToHashSet(StringComparer.Ordinal);

        ProviderScopedRoles.All.Should().BeSubsetOf(onProviderRules);
    }

    [Fact]
    public void A_role_bound_to_nothing_in_particular_is_not_provider_scoped()
    {
        // Reception and finance are provider-independent by design. If either ever appeared here it would
        // mean a rule had quietly acquired provider-ownership, which is a real change worth failing on.
        ProviderScopedRoles.Requires("reception").Should().BeFalse();
        ProviderScopedRoles.Requires("finance").Should().BeFalse();
    }

    [Fact]
    public void IntersectWith_reports_only_the_provider_scoped_roles_it_was_given()
    {
        var held = new[] { "pharmacist", "reception", "finance" };
        ProviderScopedRoles.IntersectWith(held).Should().Equal("pharmacist");
    }

    [Fact]
    public void IntersectWith_is_empty_for_a_user_who_needs_no_provider()
    {
        ProviderScopedRoles.IntersectWith(["reception", "call_center"]).Should().BeEmpty();
    }
}
