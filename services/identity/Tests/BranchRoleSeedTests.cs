using FluentAssertions;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

/// <summary>
/// 25.1 — the branch-management roles as the STORE actually resolves them (design 42 §1, ADR-0029).
///
/// <c>BranchRoleScopeParityTests</c> in libs/authz proves the same invariant against the migration TEXT, and
/// runs everywhere. This one proves the resolved runtime state, which is the thing tokens are minted from —
/// a migration that parses correctly and fails to apply, or a later migration that revokes a grant, would
/// pass there and fail here. Two readings of one invariant, deliberately not sharing a code path.
/// </summary>
[Collection("identity-db")]
public class BranchRoleSeedTests
{
    private const string Coordinator = "branch_coordinator";
    private const string Manager = "clinics_manager";

    [Fact]
    public void Both_branch_roles_are_in_the_frozen_vocabulary()
    {
        IdentityContract.Roles.Should().Contain(Coordinator).And.Contain(Manager);
    }

    [Fact]
    public void The_four_branch_scopes_are_in_the_frozen_vocabulary()
    {
        IdentityContract.Scopes.Should().Contain([
            "branch:practitioner:write", "branch:roster:write",
            "branch:inventory:read", "branch:inventory:write",
        ]);
    }

    [Fact]
    public void The_branch_scopes_are_interactive_not_service_only()
    {
        // A coordinator uses these from a browser. Landing them in ServiceScopes would make them
        // unrequestable by the SPA and the role would be silently powerless — 19.7 found that class of
        // defect three times in one session.
        foreach (var s in new[] { "branch:practitioner:write", "branch:roster:write", "branch:inventory:read", "branch:inventory:write" })
        {
            IdentityContract.ServiceScopes.Should().NotContain(s);
            IdentityContract.InteractiveScopes.Should().Contain(s);
        }
    }

    [SkippableFact]
    public async Task THE_INVARIANT_the_two_branch_roles_resolve_to_identical_scope_sets()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();
        var resolver = new RoleScopeResolver(db);

        var coordinator = await resolver.ResolveScopesAsync([Coordinator]);
        var manager = await resolver.ResolveScopesAsync([Manager]);

        coordinator.Should().NotBeEmpty("a seeded role with no resolved scopes is assignable and powerless");

        // Reported in both directions: "which of them gained something" is the first question on failure.
        manager.Except(coordinator).Should().BeEmpty(
            "clinics_manager resolves a scope branch_coordinator lacks — one permission set, two reaches");
        coordinator.Except(manager).Should().BeEmpty(
            "branch_coordinator resolves a scope clinics_manager lacks — the supervisor of six clinics would " +
            "be able to do less than the coordinator of one");
    }

    [SkippableFact]
    public async Task Neither_branch_role_resolves_provider_write()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();
        var resolver = new RoleScopeResolver(db);

        foreach (var role in new[] { Coordinator, Manager })
        {
            var scopes = await resolver.ResolveScopesAsync([role]);
            scopes.Should().NotContain("provider:write", "design 42 §7 rule 3 ('{0}')", role);
            scopes.Should().NotContain("provider:admin");
            scopes.Should().NotContain("emr:read", "'{0}' runs the clinic; it does not read clinical notes", role);
        }
    }

    [SkippableFact]
    public async Task The_branch_roles_resolve_receptions_twelve_plus_the_four()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();
        var resolver = new RoleScopeResolver(db);

        var expected = new[]
        {
            "reception:search", "reception:read", "eligibility:check", "appointment:read", "appointment:write",
            "patient:read", "practitioner:read", "note:read", "profile:read", "callcentre:history:read",
            "notification:read", "claims:reimburse:submit",
            "branch:practitioner:write", "branch:roster:write", "branch:inventory:read", "branch:inventory:write",
        };

        (await resolver.ResolveScopesAsync([Coordinator])).Should().BeEquivalentTo(expected);
        (await resolver.ResolveScopesAsync([Manager])).Should().BeEquivalentTo(expected);
    }

    [SkippableFact]
    public async Task Both_roles_exist_in_the_store_with_a_sensitivity_tier()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        var rows = await db.Roles.AsNoTracking()
            .Where(r => r.Name == Coordinator || r.Name == Manager)
            .Select(r => new { r.Name, r.SensitivityTier }).ToListAsync();

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.SensitivityTier == "T2",
            "they administer staff licence data and clinic stock, never a diagnosis");
    }
}
