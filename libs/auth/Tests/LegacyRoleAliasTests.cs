using System.Security.Claims;
using FluentAssertions;

namespace Mersal.Auth.Tests;

/// <summary>
/// 29.1 — the dual-accept window for the imaging_tech → radiology_tech rename (design 45 §1).
///
/// <para>These are the tests that decide whether the rename can be deployed without an outage. The rename
/// itself is trivial; surviving the 300 s in which both spellings are in flight is not.</para>
/// </summary>
public class LegacyRoleAliasTests
{
    [Fact]
    public void A_token_minted_before_the_switch_still_authorises_under_the_new_name()
    {
        // The case that breaks a naive rename: a technician signed in one second before deploy holds a token
        // naming imaging_tech for the next 300 s, and every service it reaches now checks radiology_tech.
        var principal = PrincipalWithRoles("imaging_tech");

        principal.IsInRole("radiology_tech").Should().BeTrue();
        principal.IsInRole("imaging_tech").Should().BeTrue("the old name must not stop working mid-window");
    }

    [Fact]
    public void A_token_minted_after_the_switch_still_authorises_at_a_service_not_yet_redeployed()
    {
        // The case a one-way legacy→canonical normalisation would break, and the one that happens on EVERY
        // rollout rather than only in the first 300 s: services are independently deployable, so the switched
        // issuer mints radiology_tech while orders-service is still checking imaging_tech.
        var principal = PrincipalWithRoles("radiology_tech");

        principal.IsInRole("imaging_tech").Should().BeTrue();
        principal.IsInRole("radiology_tech").Should().BeTrue();
    }

    [Fact]
    public void Aliasing_grants_no_role_that_was_not_already_on_the_token()
    {
        // An alias maps a name to a name. It must never be a way for authority to enter the token.
        var principal = PrincipalWithRoles("imaging_tech");

        principal.Roles.Should().BeEquivalentTo("imaging_tech", "radiology_tech");
        principal.IsInRole("lab_tech").Should().BeFalse();
        principal.IsInRole("pharmacist").Should().BeFalse();
        principal.IsInRole("super_admin").Should().BeFalse();
    }

    [Fact]
    public void An_unaliased_role_is_untouched()
    {
        var principal = PrincipalWithRoles("doctor", "reception");

        principal.Roles.Should().BeEquivalentTo("doctor", "reception");
    }

    [Fact]
    public void Canonical_resolves_the_legacy_name_and_leaves_everything_else_alone()
    {
        LegacyRoleAliases.Canonical("imaging_tech").Should().Be("radiology_tech");
        LegacyRoleAliases.Canonical("radiology_tech").Should().Be("radiology_tech");
        LegacyRoleAliases.Canonical("doctor").Should().Be("doctor");
    }

    [Fact]
    public void Expansion_is_idempotent()
    {
        // Expanding an already-expanded set must not grow it further — the boundary can be crossed twice in a
        // request that re-derives a principal, and a set that grows each time is a set that eventually differs
        // from itself.
        var once = LegacyRoleAliases.Expand(["imaging_tech"]);
        var twice = LegacyRoleAliases.Expand(once);

        twice.Should().BeEquivalentTo(once);
    }

    [Fact]
    public void The_window_is_open_until_the_contract_step_empties_the_table()
    {
        // A canary, not a feature test. When this goes red the contract step has landed, and every dual-accept
        // artefact named in docs/runbooks/radiology-rename.md must go with it — including this test file.
        LegacyRoleAliases.WindowOpen.Should().BeTrue(
            "the imaging_tech alias is still live; see docs/runbooks/radiology-rename.md for the contract step");
    }

    private static HbmpPrincipal PrincipalWithRoles(params string[] roles)
    {
        var claims = new List<Claim> { new("sub", "7c5b0a2e-2f61-4a9d-8f7a-1b6e2d3c4a55") };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        return HbmpPrincipal.FromClaims(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")));
    }
}
