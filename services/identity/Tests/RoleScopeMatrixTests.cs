using FluentAssertions;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

/// <summary>The roles/scopes-as-data seed + resolver (17.1). Proves the store carries the frozen role
/// vocabulary and that role→scope resolution matches the min-necessary matrix — including the hard rules
/// (Reception/Finance carry NO clinical scope; Lab carries NO pharmacy/rx scope).</summary>
[Collection("identity-db")]
public class RoleScopeMatrixTests
{
    [Fact]
    public void Frozen_role_vocabulary_is_23_distinct_roles()
    {
        // 25.1 added branch_coordinator + clinics_manager (design 42 §1).
        // 29.1 added radiology_tech (design 45 §1) — a RENAME of imaging_tech, so the count is temporarily
        // inflated by one. It drops back at the contract step, when imaging_tech is dropped.
        // 29.2b added procedure_provider (design 45 §2b) — a genuinely NEW role, the external delivering
        // provider. So 21 + 1 rename-in-flight + 1 new = 23, becoming 22 after the contract step.
        IdentityContract.Roles.Should().HaveCount(23);
        IdentityContract.Roles.Distinct().Should().HaveCount(23);
        IdentityContract.Roles.Should().OnlyContain(r => !r.Any(char.IsUpper));
    }

    [Fact]
    public void Both_spellings_of_the_radiology_role_are_seeded_for_the_rename_window()
    {
        // 29.1 / design 45 §1 — the store must be able to grant EITHER name until the window closes. A token
        // minted before the switch names imaging_tech for the rest of its 300 s TTL, and a role the store has
        // already dropped cannot be resolved to its scopes: the technician authenticates, resolves to nothing
        // and is shown an account with no portal. Dropping imaging_tech is the CONTRACT step and belongs on a
        // later deploy — see docs/runbooks/radiology-rename.md.
        IdentityContract.Roles.Should().Contain("radiology_tech");
        IdentityContract.Roles.Should().Contain("imaging_tech",
            "the legacy spelling stays grantable until the dual-accept window closes");
    }

    [SkippableFact]
    public async Task Seed_scope_catalog_equals_the_frozen_vocabulary()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        var seeded = await db.Scopes.AsNoTracking().Select(s => s.Name).ToListAsync();
        seeded.Should().BeEquivalentTo(IdentityContract.Scopes,
            "the DB scope catalog must equal the frozen contract vocabulary the issuer registers");
    }

    [SkippableFact]
    public async Task Seed_contains_exactly_the_frozen_roles()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        var names = await db.Roles.AsNoTracking().Select(r => r.Name!).ToListAsync();
        names.Should().BeEquivalentTo(IdentityContract.Roles);
    }

    [SkippableFact]
    public async Task Every_role_resolves_to_a_non_empty_subset_of_the_scope_catalog()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();
        var resolver = new RoleScopeResolver(db);
        var catalog = (await db.Scopes.AsNoTracking().Select(s => s.Name).ToListAsync()).ToHashSet(StringComparer.Ordinal);

        foreach (var role in IdentityContract.Roles)
        {
            var scopes = await resolver.ResolveScopesAsync([role]);
            scopes.Should().NotBeEmpty($"role '{role}' must grant at least one scope");
            scopes.Should().BeSubsetOf(catalog, $"role '{role}' must only grant catalog scopes");
            scopes.Should().Contain("notification:read", $"every human role has the in-app inbox ('{role}')");
        }
    }

    [SkippableFact]
    public async Task Resolver_unions_multiple_roles_and_returns_empty_for_unknown()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();
        var resolver = new RoleScopeResolver(db);

        var finance = await resolver.ResolveScopesAsync(["finance"]);
        var reception = await resolver.ResolveScopesAsync(["reception"]);
        var union = await resolver.ResolveScopesAsync(["finance", "RECEPTION"]); // case-insensitive on role

        union.Should().BeEquivalentTo(finance.Concat(reception).ToHashSet(StringComparer.Ordinal));
        (await resolver.ResolveScopesAsync(["not_a_role"])).Should().BeEmpty();
        (await resolver.ResolveScopesAsync([])).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Min_necessary_hard_rules_hold_in_the_data()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();
        var resolver = new RoleScopeResolver(db);

        // Reception is front-desk: no clinical reach.
        var reception = await resolver.ResolveScopesAsync(["reception"]);
        reception.Should().NotContain(s => s.StartsWith("emr:", StringComparison.Ordinal));
        reception.Should().NotContain("rx:write");

        // Finance ≠ diagnosis: no emr/clinical scope.
        var finance = await resolver.ResolveScopesAsync(["finance"]);
        finance.Should().NotContain(s => s.StartsWith("emr:", StringComparison.Ordinal));

        // Lab: no pharmacy / prescription scope.
        var lab = await resolver.ResolveScopesAsync(["lab_tech"]);
        lab.Should().NotContain(s => s.StartsWith("pharmacy:", StringComparison.Ordinal));
        lab.Should().NotContain("rx:write");

        // Call centre: no clinical reach by construction.
        var callCentre = await resolver.ResolveScopesAsync(["call_center"]);
        callCentre.Should().NotContain(s => s.StartsWith("emr:", StringComparison.Ordinal));
    }
}
