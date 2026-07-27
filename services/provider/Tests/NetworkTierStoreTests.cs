using FluentAssertions;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProviderEntity = Mersal.Provider.Domain.Provider;

namespace Mersal.Provider.Tests;

/// <summary>
/// Phase 19.1b at the datastore (env-gated <c>PROVIDER_TEST_DB_OWNER</c>, migration 0008 applied).
///
/// The endpoints translate these into 409s, but the endpoint is not the guarantee — a repair script or a psql
/// session walks straight past it. Every constraint here is attempted DIRECTLY through EF with no endpoint in
/// the way, which is the only way to know the invariant is structural:
///
/// <list type="bullet">
/// <item>a provider cannot sit in two tiers on the same day (else resolution has two right answers);</item>
/// <item>abutting windows ARE allowed, because a tier move must leave no uncovered day;</item>
/// <item>at most one Active out-of-network tier exists, so "fail safe" has exactly one place to fall to.</item>
/// </list>
///
/// Every test scopes itself to a throwaway tenant and cleans up, so they need no serialization — matching the
/// existing provider DB suites, which do the same rather than share a collection.
/// </summary>
public class NetworkTierStoreTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_OWNER");

    private static ProviderDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ProviderDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private static string T() => $"tier-test-{Guid.NewGuid():N}";

    // ---- the overlap exclusion -------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_provider_cannot_be_in_two_tiers_on_the_same_day()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");
        var tenant = T();
        try
        {
            var (providerId, t1, t2) = await Seed(tenant);
            await Insert(Assignment(tenant, t1, providerId, new(2026, 1, 1)));

            await using var db = Ctx();
            db.NetworkAssignments.Add(Assignment(tenant, t2, providerId, new(2026, 6, 1)));

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be("23P01", "an exclusion violation — the open-ended T1 window already covers June");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Abutting_windows_are_allowed_so_a_tier_move_leaves_no_uncovered_day()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");
        var tenant = T();
        try
        {
            var (providerId, t1, t2) = await Seed(tenant);
            // [Jan, Mar) then [Mar, ∞): the half-open ranges touch but do not overlap.
            await Insert(Assignment(tenant, t1, providerId, new(2026, 1, 1), to: new(2026, 3, 1)));
            await Insert(Assignment(tenant, t2, providerId, new(2026, 3, 1)));

            await using var db = Ctx();
            var rows = await db.NetworkAssignments.AsNoTracking()
                .Where(a => a.TenantId == tenant).ToListAsync();
            rows.Should().HaveCount(2);

            // And the resolver reads the boundary the same way the constraint does.
            var tiers = await db.NetworkTiers.AsNoTracking().Where(t => t.TenantId == tenant)
                .ToDictionaryAsync(t => t.NetworkTierId, t => t);
            NetworkTierResolution.Resolve(rows, tiers, new(2026, 2, 28))!.Tier.NetworkTierId.Should().Be(t1);
            NetworkTierResolution.Resolve(rows, tiers, new(2026, 3, 1))!.Tier.NetworkTierId.Should().Be(t2);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Different_scope_refs_may_overlap_because_that_is_how_an_override_works()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");
        var tenant = T();
        try
        {
            var (providerId, t1, t2) = await Seed(tenant);
            var locationId = Guid.NewGuid();
            await using (var db = Ctx())
            {
                db.Locations.Add(new ProviderLocation
                {
                    LocationId = locationId, ProviderId = providerId, TenantId = tenant, Name = "Branch",
                });
                await db.SaveChangesAsync();
            }

            // The provider sits in T1 and one of its locations in T2 for the SAME period — that is precisely
            // the most-specific-wins case, so the constraint must not reject it.
            await Insert(Assignment(tenant, t1, providerId, new(2026, 1, 1)));
            await Insert(Assignment(tenant, t2, providerId, new(2026, 1, 1),
                scope: NetworkAssignmentScope.Location, scopeRef: locationId));

            await using var check = Ctx();
            (await check.NetworkAssignments.AsNoTracking().CountAsync(a => a.TenantId == tenant)).Should().Be(2);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_revoked_assignment_does_not_block_its_replacement()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");
        var tenant = T();
        try
        {
            var (providerId, t1, t2) = await Seed(tenant);
            var wrong = Assignment(tenant, t1, providerId, new(2026, 1, 1));
            wrong.Status = NetworkAssignmentStatus.Revoked;
            wrong.RevokedReason = "assigned to the wrong provider";
            await Insert(wrong);

            // A revoked row never governed anything, so it must not stand in the way of the correct one.
            await Insert(Assignment(tenant, t2, providerId, new(2026, 1, 1)));

            await using var db = Ctx();
            (await db.NetworkAssignments.AsNoTracking()
                .CountAsync(a => a.TenantId == tenant && a.Status == NetworkAssignmentStatus.Active)).Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    // ---- the single out-of-network default ---------------------------------------------------------------

    [SkippableFact]
    public async Task Only_one_active_out_of_network_tier_may_exist()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");
        var tenant = T();
        try
        {
            await using (var db = Ctx())
            {
                db.NetworkTiers.Add(Tier(tenant, "OON", rank: 90, oon: true));
                await db.SaveChangesAsync();
            }

            await using var second = Ctx();
            second.NetworkTiers.Add(Tier(tenant, "OON2", rank: 91, oon: true));

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("uq_network_tier_single_oon",
                    "resolution falls back to THE out-of-network tier; two would make that fallback a coin toss");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Two_active_tiers_may_not_share_a_rank()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");
        var tenant = T();
        try
        {
            await using (var db = Ctx())
            {
                db.NetworkTiers.Add(Tier(tenant, "T1", rank: 1));
                await db.SaveChangesAsync();
            }

            await using var second = Ctx();
            second.NetworkTiers.Add(Tier(tenant, "GOLD", rank: 1));

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("uq_network_tier_rank", "rank is what 'most preferred' means");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_retired_tier_frees_its_rank_and_the_out_of_network_slot()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");
        var tenant = T();
        try
        {
            await using (var db = Ctx())
            {
                var retired = Tier(tenant, "OON-OLD", rank: 90, oon: true);
                retired.Status = NetworkTierStatus.Retired;
                db.NetworkTiers.Add(retired);
                await db.SaveChangesAsync();
            }

            // Both partial indexes are scoped to Active rows, so the network can be restructured without
            // deleting the tiers that priced last year's claims.
            await using var db2 = Ctx();
            db2.NetworkTiers.Add(Tier(tenant, "OON", rank: 90, oon: true));
            await db2.SaveChangesAsync();

            (await db2.NetworkTiers.AsNoTracking().CountAsync(t => t.TenantId == tenant)).Should().Be(2);
        }
        finally { await Cleanup(tenant); }
    }

    // ---- fixtures ----------------------------------------------------------------------------------------

    private static NetworkTier Tier(string tenant, string code, int rank, bool oon = false) => new()
    {
        NetworkTierId = Guid.NewGuid(), TenantId = tenant, TierCode = code,
        NameEn = code, NameAr = code, Rank = rank, IsOutOfNetwork = oon,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ProviderNetworkAssignment Assignment(
        string tenant, Guid tierId, Guid providerId, DateOnly from, DateOnly? to = null,
        NetworkAssignmentScope scope = NetworkAssignmentScope.Provider, Guid? scopeRef = null) => new()
    {
        AssignmentId = Guid.NewGuid(), TenantId = tenant, NetworkTierId = tierId, ProviderId = providerId,
        Scope = scope, ScopeRef = scopeRef ?? providerId, EffectiveFrom = from, EffectiveTo = to,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task Insert(ProviderNetworkAssignment a)
    {
        await using var db = Ctx();
        db.NetworkAssignments.Add(a);
        await db.SaveChangesAsync();
    }

    /// <summary>A provider plus two tiers, all scoped to a throwaway tenant.</summary>
    private static async Task<(Guid ProviderId, Guid T1, Guid T2)> Seed(string tenant)
    {
        await using var db = Ctx();
        var provider = new ProviderEntity
        {
            ProviderId = Guid.NewGuid(), TenantId = tenant, ProviderCode = $"P{Guid.NewGuid():N}"[..12],
            LegalName = "Test Hospital", ProviderType = ProviderType.Hospital,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var t1 = Tier(tenant, "T1", rank: 1);
        var t2 = Tier(tenant, "T2", rank: 2);
        db.Providers.Add(provider);
        db.NetworkTiers.AddRange(t1, t2);
        await db.SaveChangesAsync();
        return (provider.ProviderId, t1.NetworkTierId, t2.NetworkTierId);
    }

    /// <summary>Ordered raw SQL: the assignment→tier and assignment→provider FKs are real in the database but
    /// have no EF navigation, so the change tracker cannot order these deletes itself.</summary>
    private static async Task Cleanup(string tenant)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM provider.provider_network_assignment WHERE tenant_id = {0}", tenant);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM provider.provider_location WHERE tenant_id = {0}", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM provider.network_tier WHERE tenant_id = {0}", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM provider.provider_history WHERE provider_id IN " +
                                             "(SELECT provider_id FROM provider.provider WHERE tenant_id = {0})", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM provider.provider WHERE tenant_id = {0}", tenant);
    }
}
