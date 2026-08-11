using FluentAssertions;
using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// The sweep that stops this suite leaking fixtures into the shared development database.
///
/// <para>
/// ============================================================================================================
/// WHY THE CLEANUP NEEDS ITS OWN TESTS
/// ============================================================================================================
/// <see cref="TestFixtureSweep"/> deletes rows nobody asked it to delete, on a database a person is also
/// using. That is a reasonable thing to do only while its predicate is exactly right, and a predicate written
/// once and never exercised is a predicate that drifts — the role pattern in particular sits one careless
/// edit away from matching a role an administrator designed by hand in the Access Catalogue.
/// </para>
///
/// <para>
/// So the test that matters here is not "it deletes the fixture". It is
/// <see cref="It_does_not_touch_an_account_that_could_belong_to_a_real_person"/>: a sweep that misses a
/// fixture leaves one stray row for the next run to clear, while a sweep that is too greedy destroys somebody
/// else's account and there is nothing to restore it from.
/// </para>
/// </summary>
[Collection("identity-db")]
public class TestFixtureSweepTests(IdentityHostFixture host) : IClassFixture<IdentityHostFixture>
{
    [SkippableFact]
    public async Task It_removes_a_fixture_account_the_owning_test_never_got_to_clean_up()
    {
        Skip.If(IdentityTestDb.Conn is null);
        // Exactly the shape a killed run leaves behind: seeded by the harness, and then nothing.
        var name = $"sweepme-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, "Test-Passw0rd!", ["reception"]);

        TestFixtureSweep.Run("a test");

        await using var db = IdentityTestDb.NewContext();
        (await db.Users.AsNoTracking().AnyAsync(u => u.Id == id)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task It_takes_the_membership_with_it_rather_than_leaving_an_orphan()
    {
        Skip.If(IdentityTestDb.Conn is null);
        // `SeedUser` mirrors a membership, and the roster asserts over that table. A sweep that removed the
        // account and left its membership would trade one kind of litter for a worse one.
        var name = $"sweepmembership-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, "Test-Passw0rd!", ["reception"]);

        TestFixtureSweep.Run("a test");

        await using var db = IdentityTestDb.NewContext();
        (await db.Memberships.AsNoTracking().AnyAsync(m => m.UserId == id)).Should().BeFalse();
    }

    /// <summary>
    /// THE ONE THAT MUST NEVER GO RED.
    ///
    /// <para>A false negative costs one leftover row until the next run. A false positive deletes a real
    /// account — and every membership, session and grant hanging off it — from a database somebody is
    /// working in.</para>
    /// </summary>
    [SkippableFact]
    public async Task It_does_not_touch_an_account_that_could_belong_to_a_real_person()
    {
        Skip.If(IdentityTestDb.Conn is null);
        using var scope = host.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // A `@mersal.local` address, which is what the demo seeder and every real account carry. The name
        // still carries a GUID so this test can find and remove its own subject afterwards — proving that the
        // ADDRESS is what the sweep matches on, not the name.
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"realperson-{Guid.NewGuid():N}",
            Email = $"realperson-{Guid.NewGuid():N}@mersal.local",
            TenantId = TestFlow.TenantA, DisplayName = "Real Person",
            CreatedAt = DateTimeOffset.UtcNow, EmailConfirmed = true,
        };
        (await users.CreateAsync(user, "Test-Passw0rd!")).Succeeded.Should().BeTrue();

        try
        {
            TestFixtureSweep.Run("a test");

            await using var db = IdentityTestDb.NewContext();
            (await db.Users.AsNoTracking().AnyAsync(u => u.Id == user.Id))
                .Should().BeTrue("the sweep matches the RFC 2606 test domain, not every account with a GUID in its name");
        }
        finally { await TestFlow.DeleteUser(host.Factory, user.Id); }
    }

    /// <summary>
    /// The Access Catalogue's role designer writes tenant-owned roles to this same database, so being
    /// tenant-owned cannot be enough to be swept. The hex tail is what separates a fixture from a decision.
    /// </summary>
    [SkippableFact]
    public async Task It_spares_a_tenant_role_an_administrator_designed_by_hand()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var db = IdentityTestDb.NewContext();

        var minted = $"sweeprole_{Guid.NewGuid():N}";   // as the catalogue tests mint them
        // As a person would name one — so it cannot carry a GUID, so it cannot be unique per run. If a
        // previous run was killed between the insert and the cleanup, the row is still here and the unique
        // index would fail this test for the one reason it is not about. Cleared first, deliberately.
        const string byHand = "sweeprole_byhand";
        db.Roles.RemoveRange(await db.Roles.Where(r => r.Name == byHand).ToListAsync());
        await db.SaveChangesAsync();

        db.Roles.Add(new ApplicationRole { Id = Guid.NewGuid(), Name = minted, NormalizedName = minted.ToUpperInvariant(), OwnerTenantId = TestFlow.TenantA });
        db.Roles.Add(new ApplicationRole { Id = Guid.NewGuid(), Name = byHand, NormalizedName = byHand.ToUpperInvariant(), OwnerTenantId = TestFlow.TenantA });
        await db.SaveChangesAsync();

        try
        {
            TestFixtureSweep.Run("a test");

            await using var check = IdentityTestDb.NewContext();
            (await check.Roles.AsNoTracking().AnyAsync(r => r.Name == minted)).Should().BeFalse("it carries a minted GUID tail");
            (await check.Roles.AsNoTracking().AnyAsync(r => r.Name == byHand)).Should().BeTrue("somebody chose this name");
        }
        finally
        {
            await using var cleanup = IdentityTestDb.NewContext();
            var left = await cleanup.Roles.Where(r => r.Name == byHand || r.Name == minted).ToListAsync();
            cleanup.Roles.RemoveRange(left);
            await cleanup.SaveChangesAsync();
        }
    }

    [Fact]
    public void It_is_a_no_op_rather_than_a_failure_when_there_is_no_database()
    {
        // The DB-less CI run reaches the collection fixture like any other. Housekeeping that threw there
        // would turn "these tests skip on this machine" into a red build.
        if (IdentityTestDb.Conn is not null) return;
        var act = () => TestFixtureSweep.Run("a test");
        act.Should().NotThrow();
    }
}
