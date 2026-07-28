using FluentAssertions;
using Mersal.Authz;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.2 PARITY — invariant 5 (design 40 §5, §7).
///
/// The effective set is computed in two places: at token issuance (mode 1) and out-of-session from the
/// store (mode 2). Two implementations of one algebra drifting apart is the standing risk the design
/// names, and this fixture matrix is the mitigation: roles × allows × denies × expiry × deprecated ×
/// platform-admin × no-membership, every case run through BOTH modes, asserting identical sets.
///
/// DO NOT DELETE OR SKIP. <see cref="AuthzParityGuardTests"/> pins this class by name so removing it fails
/// the build rather than quietly removing the only thing that keeps the two modes honest.
///
/// Env-gated on IDENTITY_TEST_DB against a migrated database. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class EffectiveSetParityTests
{
    /// <summary>One row of the matrix.</summary>
    /// <param name="Name">Readable label, so a failure names the case rather than an index.</param>
    /// <param name="Roles">Roles the membership holds.</param>
    /// <param name="Allows">Allow overrides, with optional expiry offsets from "now".</param>
    /// <param name="Denies">Deny overrides, with optional expiry offsets from "now".</param>
    /// <param name="PlatformAdmin">Whether the identity carries the platform-administration flag.</param>
    public sealed record Case(
        string Name,
        string[] Roles,
        (string Key, TimeSpan? In)[] Allows,
        (string Key, TimeSpan? In)[] Denies,
        bool PlatformAdmin = false);

    public static TheoryData<Case> Matrix()
    {
        var past = TimeSpan.FromHours(-1);
        var future = TimeSpan.FromHours(1);
        return new TheoryData<Case>
        {
            new Case("no roles, no overrides", [], [], []),
            new Case("roles only", ["finance"], [], []),
            new Case("two roles union", ["finance", "reception"], [], []),
            new Case("allow adds a key", ["reception"], [("finance:read", null)], []),
            // "deny wins" expressed the only way the STORE can represent it. `ux_override_membership_scope`
            // permits one live override per (membership, key), so a simultaneous Allow and Deny on one key
            // is unrepresentable here by construction — the role grant is the other allow-source, and this
            // is the conflict that actually occurs. The raw allow-vs-deny ordering is pinned in
            // EffectiveSetEvaluatorTests, which feeds the algebra directly and is not bound by the index.
            new Case("deny removes a role grant — deny wins", ["finance"], [], [("finance:read", null)]),
            new Case("expired allow is inert", ["reception"], [("finance:read", past)], []),
            new Case("expired deny stops withholding", ["finance"], [], [("finance:read", past)]),
            new Case("future allow applies", ["reception"], [("finance:read", future)], []),
            new Case("future deny applies", ["finance"], [], [("finance:read", future)]),
            new Case("deprecated key still resolves", ["finance"], [(DeprecatedKey, null)], []),
            new Case("platform admin, no membership roles", [], [], [], PlatformAdmin: true),
            new Case("platform admin with clinical roles", ["doctor"], [], [], PlatformAdmin: true),
            new Case("platform admin denied an admin key", [], [], [("admin:write", null)], PlatformAdmin: true),
            new Case("everything at once", ["finance", "reception"],
                [("emr:read", future), ("orders:read", past)],
                [("finance:read", null), ("reception:search", past)],
                PlatformAdmin: true),
        };
    }

    /// <summary>A real catalog key this suite marks deprecated for the duration of a test, so deprecation is
    /// exercised without inventing a key the FK would reject.</summary>
    private const string DeprecatedKey = "orders:read";

    [SkippableTheory]
    [MemberData(nameof(Matrix))]
    public async Task Both_modes_compute_identical_sets(Case c)
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        using var factory = new IdentityAppFactory();

        var uname = $"par-{Guid.NewGuid():N}";
        var (userId, _) = await TestFlow.SeedUser(factory, uname, "Passw0rd!Mersal", c.Roles);
        try
        {
            var membershipId = await TestFlow.MembershipIdOf(factory, userId, TestFlow.TenantA);
            if (c.PlatformAdmin) await SetPlatformAdmin(factory, userId, true);
            await SeedOverrides(factory, membershipId, c);

            // MODE 2 — from the store, by id, no session.
            using var scope2 = factory.Services.CreateScope();
            var mode2 = await scope2.ServiceProvider.GetRequiredService<IEffectiveSetService>()
                .ForMembershipAsync(membershipId);

            // MODE 1 — the path token issuance takes, in a SEPARATE scope so neither run can see the
            // other's cache or change tracker. A shared scope would make parity trivially true.
            using var scope1 = factory.Services.CreateScope();
            var db1 = scope1.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
            var membership = await db1.Memberships.AsNoTracking().FirstAsync(m => m.MembershipId == membershipId);
            var mode1 = await scope1.ServiceProvider.GetRequiredService<EffectiveSetService>()
                .ComputeAsync(membership, "parity-test");

            mode2.Should().NotBeNull();
            mode1.Keys.Should().BeEquivalentTo(mode2!.Keys, "case '{0}' must resolve identically in both modes", c.Name);
            mode1.DeprecatedInUse.Should().BeEquivalentTo(mode2.DeprecatedInUse,
                "deprecation reporting must not differ between modes either — it drives the migration plan");
        }
        finally { await TestFlow.DeleteUser(factory, userId); }
    }

    [SkippableFact]
    public async Task The_token_scope_claim_is_the_effective_set_not_the_raw_role_grants()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        using var factory = new IdentityAppFactory();

        var uname = $"pare-{Guid.NewGuid():N}";
        // TWO roles, and only one of their keys is denied. Denying everything the request asks for makes
        // the request unsatisfiable and 18.B3 (S5) refuses it outright — a refusal proves the override was
        // consulted, but it cannot show that the SURVIVING authority is still correct. Keeping
        // reception:search grantable means a real token comes back and the absence is observable in it.
        var (userId, _) = await TestFlow.SeedUser(factory, uname, "Passw0rd!Mersal", ["finance", "reception"]);
        try
        {
            var membershipId = await TestFlow.MembershipIdOf(factory, userId, TestFlow.TenantA);
            await SeedOverrides(factory, membershipId, new Case("", [], [], [("finance:read", null)]));

            // The whole point of mode 1: an override is not an out-of-session opinion, it reaches the
            // token. If this held only in mode 2, every service enforcing the scope claim would still be
            // honouring authority an administrator had explicitly revoked.
            var token = await TestFlow.AuthCodeToken(
                factory, uname, "Passw0rd!Mersal", null, "openid finance:read reception:search");
            var principal = await TestFlow.Validate(factory.CreateClient(), token);

            principal.Scopes.Should().Contain("reception:search", "the un-denied role grant must survive");
            principal.Scopes.Should().NotContain("finance:read",
                "a Deny override must remove the key from the ISSUED TOKEN, not merely from mode 2");
        }
        finally { await TestFlow.DeleteUser(factory, userId); }
    }

    // ---- fixtures -------------------------------------------------------------------------------------

    private static async Task SetPlatformAdmin(IdentityAppFactory factory, Guid userId, bool value)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        var u = await db.Users.FirstAsync(x => x.Id == userId);
        u.IsPlatformAdmin = value;
        await db.SaveChangesAsync();
    }

    private static async Task SeedOverrides(IdentityAppFactory factory, Guid membershipId, Case c)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();

        foreach (var (key, effect, offset) in
                 c.Allows.Select(a => (a.Key, OverrideEffect.Allow, a.In))
                     .Concat(c.Denies.Select(d => (d.Key, OverrideEffect.Deny, d.In))))
        {
            db.Overrides.Add(new MembershipOverride
            {
                OverrideId = Guid.NewGuid(), MembershipId = membershipId, ScopeKey = key, Effect = effect,
                Reason = $"parity fixture: {c.Name}", GrantedBy = "test", ValidUntil = offset is { } o ? now + o : null,
                CreatedAt = now, UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();
    }
}
