using FluentAssertions;
using Mersal.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.1 — THE acceptance test for the membership backfill (design 40 §1; phase-21 guardrail "backfill is
/// snapshot-parity: no user gains or loses access by migration alone").
///
/// Restructuring who holds authority is only safe if it moves nobody's authority. So: seed identities in the
/// shape the store is in TODAY (user_role bindings, tenant + provider on the user), snapshot each one's
/// effective scope set through the OLD path, run the backfill, then snapshot again through the NEW path
/// (membership → membership_role → role → role_scope) and require set equality. Gained scopes and lost
/// scopes are reported separately, because they are different incidents: one is a privilege escalation, the
/// other is a clinician locked out mid-shift.
///
/// Env-gated on IDENTITY_TEST_DB against a migrated database. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class MembershipBackfillParityTests
{
    /// <summary>The backfill exactly as 0010 ships it. Re-running it is the migration's own idempotency
    /// contract, so the test exercises the real statements rather than a paraphrase of them.</summary>
    private static async Task RunBackfillAsync(IdentityStoreDbContextAccessor db)
    {
        var sql = await File.ReadAllTextAsync(MigrationPath());
        // Everything from the backfill header to the grants block — the DDL above it is already applied, and
        // GRANT needs an owner role the test connection may not have.
        var start = sql.IndexOf("INSERT INTO identity.tenant_membership (", StringComparison.Ordinal);
        var end = sql.IndexOf("-- ---- Grants", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the backfill block must be findable — if 0010 was restructured, fix this test with it");
        end.Should().BeGreaterThan(start);

        await db.Context.Database.ExecuteSqlRawAsync(sql[start..end]);
    }

    private static string MigrationPath()
    {
        var dir = AppContext.BaseDirectory;
        var path = Path.Combine(dir, "Migrations", "0010_tenant_membership.sql");
        File.Exists(path).Should().BeTrue($"0010 must be copied to the test output ({path})");
        return path;
    }

    [SkippableFact]
    public async Task Backfill_moves_nobody_access_no_gains_and_no_losses()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");

        var tenantA = $"t-{Guid.NewGuid():N}"[..20];
        var tenantB = $"t-{Guid.NewGuid():N}"[..20];
        var seeded = new List<Guid>();

        await using var db = new IdentityStoreDbContextAccessor();
        try
        {
            // Roles that actually carry scopes in the seeded catalog, so parity is over a NON-EMPTY set —
            // a backfill that loses everything would otherwise pass trivially.
            var roles = await db.Context.Roles.AsNoTracking()
                .Where(r => r.Name == "doctor" || r.Name == "reception" || r.Name == "finance")
                .ToListAsync();
            roles.Should().HaveCount(3, "the seeded role catalog must contain doctor/reception/finance");

            var doctor = roles.Single(r => r.Name == "doctor");
            var reception = roles.Single(r => r.Name == "reception");
            var finance = roles.Single(r => r.Name == "finance");

            // The shapes that exist in the store today, including the awkward ones.
            var multiRole = await SeedUser(db, tenantA, active: true, providerId: null, seeded, doctor, reception);
            var single = await SeedUser(db, tenantA, active: true, providerId: Guid.NewGuid(), seeded, finance);
            var inactive = await SeedUser(db, tenantA, active: false, providerId: null, seeded, doctor);
            var otherTenant = await SeedUser(db, tenantB, active: true, providerId: null, seeded, reception);
            var noRoles = await SeedUser(db, tenantA, active: true, providerId: null, seeded);

            var before = new Dictionary<Guid, IReadOnlySet<string>>();
            foreach (var id in seeded) before[id] = await ScopesViaUserRole(db, id);

            before[multiRole].Should().NotBeEmpty("the parity assertion is worthless over an empty set");

            await RunBackfillAsync(db);

            foreach (var id in seeded)
            {
                var after = await ScopesViaMembership(db, id);
                var gained = after.Except(before[id]).OrderBy(s => s).ToList();
                var lost = before[id].Except(after).OrderBy(s => s).ToList();

                gained.Should().BeEmpty($"user {id} must not GAIN access from a migration (privilege escalation)");
                lost.Should().BeEmpty($"user {id} must not LOSE access from a migration (lock-out)");
            }

            // The membership itself must carry the identity's tenant and provider binding forward verbatim.
            var m = await db.Context.Memberships.AsNoTracking().SingleAsync(x => x.UserId == single);
            m.TenantId.Should().Be(tenantA);
            m.ProviderId.Should().NotBeNull("provider_id moves onto the membership, it is not dropped");

            // A disabled identity keeps its membership but must not be selectable — de-provisioning unchanged.
            var dis = await db.Context.Memberships.AsNoTracking().SingleAsync(x => x.UserId == inactive);
            dis.Status.Should().Be(MembershipStatus.Suspended);
            dis.IsSelectable.Should().BeFalse();

            // A user with no roles still gets a membership (they exist in the tenant) — carrying no scopes.
            (await db.Context.Memberships.AsNoTracking().AnyAsync(x => x.UserId == noRoles)).Should().BeTrue();
            (await ScopesViaMembership(db, noRoles)).Should().BeEmpty();

            // Tenants stay separated: the tenant-B user's membership is in tenant B, not tenant A.
            (await db.Context.Memberships.AsNoTracking().SingleAsync(x => x.UserId == otherTenant))
                .TenantId.Should().Be(tenantB);
        }
        finally
        {
            await Cleanup(db, seeded);
        }
    }

    [SkippableFact]
    public async Task Backfill_is_idempotent_rerunning_creates_no_duplicate_membership()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");

        var tenant = $"t-{Guid.NewGuid():N}"[..20];
        var seeded = new List<Guid>();
        await using var db = new IdentityStoreDbContextAccessor();
        try
        {
            var doctor = await db.Context.Roles.AsNoTracking().SingleAsync(r => r.Name == "doctor");
            var user = await SeedUser(db, tenant, active: true, providerId: null, seeded, doctor);

            await RunBackfillAsync(db);
            await RunBackfillAsync(db);
            await RunBackfillAsync(db);

            // The unique index is the real guarantee; this proves the INSERT guards agree with it rather than
            // relying on the index to raise.
            (await db.Context.Memberships.AsNoTracking().CountAsync(m => m.UserId == user)).Should().Be(1);
            var membership = await db.Context.Memberships.AsNoTracking().SingleAsync(m => m.UserId == user);
            (await db.Context.MembershipRoles.AsNoTracking().CountAsync(r => r.MembershipId == membership.MembershipId))
                .Should().Be(1);
        }
        finally
        {
            await Cleanup(db, seeded);
        }
    }

    [SkippableFact]
    public async Task One_identity_can_hold_two_memberships_with_different_authority()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");

        // Invariant 1, made concrete: the same person, two tenants, two different effective sets. This is the
        // capability the old user-level binding could not express at all.
        var seeded = new List<Guid>();
        await using var db = new IdentityStoreDbContextAccessor();
        try
        {
            var doctor = await db.Context.Roles.AsNoTracking().SingleAsync(r => r.Name == "doctor");
            var finance = await db.Context.Roles.AsNoTracking().SingleAsync(r => r.Name == "finance");

            var user = await SeedUser(db, $"t-{Guid.NewGuid():N}"[..20], active: true, providerId: null, seeded);

            var mA = await AddMembership(db, user, "tenant-clinic", doctor.Id);
            var mB = await AddMembership(db, user, "tenant-partner-ngo", finance.Id);

            var setA = await ScopesForMembership(db, mA);
            var setB = await ScopesForMembership(db, mB);

            setA.Should().NotBeEmpty();
            setB.Should().NotBeEmpty();
            setA.Should().NotBeEquivalentTo(setB, "two memberships of one identity must be able to differ");

            // And the identity itself grants nothing — authority lives on the membership (invariant 1).
            setA.Should().NotBeEquivalentTo(setA.Union(setB), "a membership must not inherit the other's scopes");
            setA.Union(setB).Should().HaveCountGreaterThan(setA.Count, "the union is strictly larger than either");
        }
        finally
        {
            await Cleanup(db, seeded);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>Effective scopes through the OLD path: user → user_role → role → role_scope.</summary>
    private static async Task<IReadOnlySet<string>> ScopesViaUserRole(IdentityStoreDbContextAccessor db, Guid userId)
    {
        var sql = """
            SELECT rs.scope_name
            FROM identity.user_role ur
            JOIN identity.role r       ON r.id = ur.role_id
            JOIN identity.role_scope rs ON rs.role_name = r.name
            WHERE ur.user_id = {0}
            """;
        return await QuerySet(db, sql, userId);
    }

    /// <summary>Effective scopes through the NEW path: membership → membership_role → role → role_scope.</summary>
    private static async Task<IReadOnlySet<string>> ScopesViaMembership(IdentityStoreDbContextAccessor db, Guid userId)
    {
        var sql = """
            SELECT rs.scope_name
            FROM identity.tenant_membership m
            JOIN identity.membership_role mr ON mr.membership_id = m.membership_id
            JOIN identity.role r             ON r.id = mr.role_id
            JOIN identity.role_scope rs      ON rs.role_name = r.name
            WHERE m.user_id = {0} AND NOT m.is_deleted
            """;
        return await QuerySet(db, sql, userId);
    }

    private static async Task<IReadOnlySet<string>> ScopesForMembership(IdentityStoreDbContextAccessor db, Guid membershipId)
    {
        var sql = """
            SELECT rs.scope_name
            FROM identity.membership_role mr
            JOIN identity.role r        ON r.id = mr.role_id
            JOIN identity.role_scope rs ON rs.role_name = r.name
            WHERE mr.membership_id = {0}
            """;
        return await QuerySet(db, sql, membershipId);
    }

    private static async Task<IReadOnlySet<string>> QuerySet(IdentityStoreDbContextAccessor db, string sql, Guid arg)
    {
        var conn = db.Context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, "@p0");
        var p = cmd.CreateParameter();
        p.ParameterName = "@p0";
        p.Value = arg;
        cmd.Parameters.Add(p);

        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) set.Add(reader.GetString(0));
        return set;
    }

    private static async Task<Guid> SeedUser(
        IdentityStoreDbContextAccessor db, string tenant, bool active, Guid? providerId,
        List<Guid> seeded, params ApplicationRole[] roles)
    {
        var id = Guid.NewGuid();
        var uname = $"bf-{id:N}";
        db.Context.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = uname,
            NormalizedUserName = uname.ToUpperInvariant(),
            TenantId = tenant,
            ProviderId = providerId,
            DisplayName = "Backfill Parity",
            IsActive = active,
            CreatedAt = DateTimeOffset.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        });
        await db.Context.SaveChangesAsync();

        foreach (var r in roles)
        {
            await db.Context.Database.ExecuteSqlRawAsync(
                "INSERT INTO identity.user_role (user_id, role_id) VALUES ({0}, {1})", id, r.Id);
        }

        seeded.Add(id);
        return id;
    }

    private static async Task<Guid> AddMembership(IdentityStoreDbContextAccessor db, Guid userId, string tenant, Guid roleId)
    {
        var membershipId = Guid.NewGuid();
        await db.Context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO identity.tenant_membership (membership_id, user_id, tenant_id, status, created_by)
            VALUES ({0}, {1}, {2}, 'Active', 'test')
            """, membershipId, userId, tenant);
        await db.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO identity.membership_role (membership_id, role_id) VALUES ({0}, {1})", membershipId, roleId);
        return membershipId;
    }

    private static async Task Cleanup(IdentityStoreDbContextAccessor db, List<Guid> userIds)
    {
        foreach (var id in userIds)
        {
            // membership + membership_role cascade from the user FK; user_role cascades too.
            await db.Context.Database.ExecuteSqlRawAsync(
                "DELETE FROM identity.tenant_membership_history WHERE user_id = {0}", id);
            await db.Context.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
        }
    }
}

/// <summary>Owns one context for a test and disposes it — keeps the tests free of nested using blocks.</summary>
internal sealed class IdentityStoreDbContextAccessor : IAsyncDisposable
{
    public Mersal.Identity.Infrastructure.IdentityStoreDbContext Context { get; } = IdentityTestDb.NewContext();

    public ValueTask DisposeAsync() => Context.DisposeAsync();
}
