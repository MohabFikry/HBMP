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
    public void Frozen_role_vocabulary_is_17_distinct_roles()
    {
        IdentityContract.Roles.Should().HaveCount(17);
        IdentityContract.Roles.Distinct().Should().HaveCount(17);
        IdentityContract.Roles.Should().OnlyContain(r => !r.Any(char.IsUpper));
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
