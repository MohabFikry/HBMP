using FluentAssertions;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.1b — tenant-local role→scope grants (design 40 §2, migration 0011).
///
/// The role CATALOG stays global — the token's <c>roles</c> vocabulary is frozen and ASP.NET Identity needs
/// globally unique role names — so tenant-locality lives on the GRANTS: two tenants may give the same role
/// name different scopes. These tests pin the resolution rule, which is the part that can silently
/// re-grant access if it is written carelessly.
///
/// Env-gated on IDENTITY_TEST_DB against a migrated database. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class TenantLocalRoleScopeTests
{
    private static async Task<List<string>> Seed(IdentityStoreDbContext db, string tenant, string role, params string[] scopes)
    {
        foreach (var s in scopes)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
                VALUES ({0}, {1}, {2}) ON CONFLICT DO NOTHING
                """, tenant, role, s);
        }
        return [.. scopes];
    }

    private static async Task Clean(IdentityStoreDbContext db, params string[] tenants)
    {
        foreach (var t in tenants)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM identity.role_scope WHERE tenant_id = {0}", t);
        }
    }

    [SkippableFact]
    public async Task Two_tenants_resolve_different_scopes_for_the_same_role_name()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        var tA = $"tl-a-{Guid.NewGuid():N}"[..16];
        var tB = $"tl-b-{Guid.NewGuid():N}"[..16];
        try
        {
            await Seed(db, tA, "reception", "reception:search", "appointment:read");
            await Seed(db, tB, "reception", "reception:search");

            var resolver = new RoleScopeResolver(db);

            (await resolver.ResolveScopesAsync(["reception"], tA))
                .Should().BeEquivalentTo("reception:search", "appointment:read");
            (await resolver.ResolveScopesAsync(["reception"], tB))
                .Should().BeEquivalentTo("reception:search");
        }
        finally { await Clean(db, tA, tB); }
    }

    [SkippableFact]
    public async Task Unprovisioned_tenant_falls_back_to_the_platform_default()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        // A tenant with no rows of its own must behave exactly as before 0011 — this is the backward
        // compatibility guarantee that lets 0011 ship without a flag day.
        var fresh = $"tl-new-{Guid.NewGuid():N}"[..16];
        var resolver = new RoleScopeResolver(db);

        var platformDefault = await resolver.ResolveScopesAsync(["reception"], RoleScope.PlatformDefault);
        var unprovisioned = await resolver.ResolveScopesAsync(["reception"], fresh);

        platformDefault.Should().NotBeEmpty("the seeded default bucket must grant reception something");
        unprovisioned.Should().BeEquivalentTo(platformDefault);
    }

    [SkippableFact]
    public async Task Tenant_owned_narrower_set_wins_over_the_platform_default()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        // A tenant that narrows a role must KEEP it narrowed — the platform default must not leak back in
        // and re-grant what an administrator just removed.
        //
        // KNOWN LIMITATION, stated rather than hidden: this model cannot express "this tenant grants
        // `reception` NOTHING". Absence of rows means "not provisioned" and falls back, so clearing a role
        // to empty resolves to the platform default, not to empty. Expressing a true empty grant needs an
        // explicit marker (a tombstone row, or a per-tenant provisioned flag) — 21.2 introduces per-membership
        // Deny overrides, which is the intended way to say "not this principal" and is strictly better than
        // a whole-tenant empty. See 0011's header.
        var t = $"tl-clr-{Guid.NewGuid():N}"[..16];
        try
        {
            await Seed(db, t, "finance", "finance:read");   // tenant IS provisioned (for another role)
            var resolver = new RoleScopeResolver(db);

            (await resolver.ResolveScopesAsync(["finance"], t)).Should().BeEquivalentTo("finance:read");

            // `reception` is not defined for this tenant → falls back (documented, per-role behaviour).
            var reception = await resolver.ResolveScopesAsync(["reception"], t);
            reception.Should().BeEquivalentTo(await resolver.ResolveScopesAsync(["reception"], RoleScope.PlatformDefault));

            // Now the tenant defines `reception` explicitly as a narrower set — the default must NOT leak in.
            await Seed(db, t, "reception", "reception:search");
            var narrowed = await resolver.ResolveScopesAsync(["reception"], t);
            narrowed.Should().BeEquivalentTo("reception:search");
            narrowed.Should().NotBeEquivalentTo(reception, "the tenant's own narrower set must win over the default");
        }
        finally { await Clean(db, t); }
    }

    [SkippableFact]
    public async Task Multi_role_union_mixes_tenant_owned_and_default_grants_per_role()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        var t = $"tl-mix-{Guid.NewGuid():N}"[..16];
        try
        {
            await Seed(db, t, "reception", "reception:search");   // tenant-owned, narrow
            var resolver = new RoleScopeResolver(db);

            var financeDefault = await resolver.ResolveScopesAsync(["finance"], RoleScope.PlatformDefault);
            var union = await resolver.ResolveScopesAsync(["reception", "finance"], t);

            // reception comes from the tenant (narrow), finance falls back to the default — resolution is
            // per-role, so one union may draw from both buckets.
            union.Should().Contain("reception:search");
            union.Should().BeEquivalentTo(financeDefault.Append("reception:search"));
        }
        finally { await Clean(db, t); }
    }

    [SkippableFact]
    public async Task Role_catalog_stays_global_so_the_frozen_roles_claim_is_untouched()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        // The decision recorded in 0011: tenant-locality lives on grants, NOT on the role catalog, so the
        // token's frozen role vocabulary cannot drift per tenant. If someone later adds tenant_id to
        // identity.role, this goes red — which is the intent.
        //
        // 28.9 added `owner_tenant_id`, and it is deliberately NOT the thing this test forbids: it records
        // who AUTHORED a role, not which tenant may see it. Names stay in one global namespace — which is
        // precisely what the uniqueness assertion below still proves, now over the built-ins AND the custom
        // roles together. A per-tenant namespace would show up here as two rows with one name.
        var all = await db.Roles.AsNoTracking().Select(r => new { r.Name, r.OwnerTenantId }).ToListAsync();

        all.Where(r => r.OwnerTenantId == null).Select(r => r.Name!)
            .Should().BeEquivalentTo(IdentityContract.Roles);
        all.Select(r => r.Name!)
            .Should().OnlyHaveUniqueItems("role names are globally unique — ASP.NET Identity's RoleStore requires it");
    }
}
