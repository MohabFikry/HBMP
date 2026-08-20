using FluentAssertions;
using Mersal.Auth;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Data.Tests;

/// <summary>
/// Phase 18.E2 — unit tests for <c>libs/data</c>, which the R2 audit noted is the most safety-critical
/// library on the platform and had none.
///
/// Everything tenant isolation rests on passes through two small classes here. <see cref="RlsContext"/>
/// carries the tenant for a request and <see cref="TenantStampingInterceptor"/> writes it onto new rows;
/// <see cref="RlsConnectionInterceptor"/> pushes it into the Postgres session so the policies can see it.
/// The database half is proven by 6 two-role RlsIsolationTests against real Postgres — but those need a
/// database, so on a laptop or in a DB-less job they SKIP, and the logic below was covered by nothing at all.
///
/// The stamping rule is the subtle one. Get it wrong in the permissive direction and a row is written with
/// the wrong tenant; get it wrong in the strict direction and a row is written with an EMPTY tenant, which
/// under a fail-closed policy is invisible to everyone including the person who created it — a record that
/// exists, consumed an id, and cannot be found.
/// </summary>
public class RlsInterceptorTests
{
    private sealed class Row
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private sealed class Untenanted
    {
        public int Id { get; set; }
        public string Note { get; set; } = "";
    }

    private sealed class TestDb(DbContextOptions<TestDb> o) : DbContext(o)
    {
        public DbSet<Row> Rows => Set<Row>();
        public DbSet<Untenanted> Untenanteds => Set<Untenanted>();
    }

    private static TestDb Context(RlsContext rls, string name) =>
        new(new DbContextOptionsBuilder<TestDb>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(new TenantStampingInterceptor(rls))
            .Options);

    [Fact]
    public async Task A_new_row_is_stamped_with_the_request_tenant()
    {
        var rls = new RlsContext { TenantId = "tenant-a" };
        await using var db = Context(rls, nameof(A_new_row_is_stamped_with_the_request_tenant));

        db.Rows.Add(new Row { Id = 1, Note = "x" });
        await db.SaveChangesAsync();

        db.Rows.Single().TenantId.Should().Be("tenant-a",
            "a handler that forgets to set TenantId must not write an unstamped row — under a fail-closed " +
            "policy that row is invisible to everyone, including whoever created it");
    }

    [Fact]
    public async Task An_explicit_tenant_is_never_overwritten()
    {
        // The stamper fills a BLANK. It must not rewrite a value the caller set deliberately — a Super Admin
        // acting cross-tenant (18.B2) sets the target tenant explicitly, and silently replacing it with the
        // caller's own would write the row to the wrong place while reporting success.
        var rls = new RlsContext { TenantId = "tenant-a" };
        await using var db = Context(rls, nameof(An_explicit_tenant_is_never_overwritten));

        db.Rows.Add(new Row { Id = 1, TenantId = "tenant-b", Note = "deliberate" });
        await db.SaveChangesAsync();

        db.Rows.Single().TenantId.Should().Be("tenant-b");
    }

    [Fact]
    public async Task An_insert_with_no_tenant_to_stamp_is_refused()
    {
        // THIS ASSERTION WAS REVERSED, deliberately, and the reason matters more than the change.
        //
        // It used to assert the row was written with an empty tenant, on the stated reasoning that "leaving
        // the value alone lets the database's NOT NULL / policy reject it loudly". The database does not
        // reject it: '' is a perfectly good string, the column is NOT NULL and satisfied, and the RLS policy
        // compares `tenant_id = current_setting(...)` which simply never matches a real tenant. So the row
        // was accepted in silence and belonged to nobody — invisible to every real tenant, visible to any
        // session binding an empty one. 1,191 rows across seven tables were found that way on the dev
        // database, and the test asserting the behaviour is the reason nobody looked.
        //
        // The old comment was not careless; it was an assumption about the database that was never checked.
        // A write with no tenant to stamp is a bug in the caller, and it is named here rather than left as
        // an orphan row for someone to find later with no way to tell whose it was.
        var rls = new RlsContext();   // TenantId = ""
        await using var db = Context(rls, nameof(An_insert_with_no_tenant_to_stamp_is_refused));

        db.Rows.Add(new Row { Id = 1, Note = "x" });

        var act = async () => await db.SaveChangesAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*belong to no tenant*",
                "the exception has to say what the row would have BEEN, not just that a field was empty — " +
                "the reader needs to know this is an isolation problem, not a validation nit");
    }

    [Fact]
    public async Task An_entity_that_sets_its_own_tenant_is_still_accepted_with_no_ambient_one()
    {
        // The refusal above is about rows that would belong to NOBODY. A background worker that stamps the
        // tenant from the event it is processing — which is what eligibility's consumer does — has answered
        // the question already, and must not be blocked by the absence of a request principal.
        var rls = new RlsContext();   // no ambient tenant
        await using var db = Context(rls, nameof(An_entity_that_sets_its_own_tenant_is_still_accepted_with_no_ambient_one));

        db.Rows.Add(new Row { Id = 1, TenantId = "from-the-event", Note = "x" });
        await db.SaveChangesAsync();

        db.Rows.Single().TenantId.Should().Be("from-the-event");
    }

    [Fact]
    public async Task An_untenanted_entity_is_untouched_when_no_tenant_is_bound()
    {
        // Entities with no TenantId column are not tenant-scoped at all — a code catalogue, a dedupe
        // ledger. They must keep saving without a tenant, or the guard above would stop the platform
        // booting rather than stop it losing rows.
        var rls = new RlsContext();
        await using var db = Context(rls, nameof(An_untenanted_entity_is_untouched_when_no_tenant_is_bound));

        db.Untenanteds.Add(new Untenanted { Id = 1, Note = "no tenant column" });
        await db.SaveChangesAsync();

        db.Untenanteds.Single().Note.Should().Be("no tenant column");
    }

    [Fact]
    public async Task An_updated_row_keeps_its_original_tenant()
    {
        // Only ADDED entries are stamped. Re-stamping on update would let a cross-tenant read followed by a
        // save quietly MOVE a row into the reader's tenant.
        var rls = new RlsContext { TenantId = "tenant-a" };
        var name = nameof(An_updated_row_keeps_its_original_tenant);
        await using (var seed = Context(new RlsContext { TenantId = "tenant-b" }, name))
        {
            seed.Rows.Add(new Row { Id = 1, Note = "original" });
            await seed.SaveChangesAsync();
        }

        await using var db = Context(rls, name);
        var row = db.Rows.Single();
        row.Note = "edited";
        await db.SaveChangesAsync();

        db.Rows.Single().TenantId.Should().Be("tenant-b");
    }

    [Fact]
    public async Task An_entity_without_a_tenant_property_is_left_alone()
    {
        // Ledgers and sequences have no tenant column by design (see the RLS-free list in the architecture
        // tests). The stamper must not throw or invent a property for them.
        var rls = new RlsContext { TenantId = "tenant-a" };
        await using var db = Context(rls, nameof(An_entity_without_a_tenant_property_is_left_alone));

        db.Untenanteds.Add(new Untenanted { Id = 1, Note = "ledger" });
        var save = async () => await db.SaveChangesAsync();

        await save.Should().NotThrowAsync();
    }

    [Fact]
    public void The_context_starts_empty_so_an_unbound_request_is_fail_closed()
    {
        // The default matters: an empty GUC matches no row under the fail-closed policies 18.B2 established.
        // If this defaulted to a tenant, a request that never reached UseHbmpRls would silently read as that
        // tenant instead of reading nothing.
        var rls = new RlsContext();
        rls.TenantId.Should().BeEmpty();
        rls.ProviderId.Should().BeEmpty();
    }

    // ---- provider scope: absence must not be the grant ----------------------------------------------
    //
    // The provider RLS policies read an empty app.provider_id as "tenant-wide", which is correct for the
    // Network Team and platform admins. It also made an ABSENT claim a grant: a provider-scoped token that
    // lost its provider_id bound "" and was handed read of every provider's rows — the inverse of what the
    // tenant sentinel above exists to prevent, in the layer that is supposed to hold when the others fail.

    private static HbmpPrincipal Principal(string role, string? providerId) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(),
        TenantId = "tenant-a", ProviderId = providerId,
    };

    [Theory]
    [InlineData("provider_admin")]
    [InlineData("lab_tech")]
    [InlineData("imaging_tech")]
    [InlineData("radiology_tech")]
    [InlineData("pharmacist")]
    public void A_provider_scoped_role_is_recognised_as_provider_scoped(string role)
    {
        // The list has to be complete: a role missing from it reads as tenant-wide when its claim goes
        // absent, which is exactly the failure this guards.
        Principal(role, providerId: null).IsProviderScoped().Should().BeTrue();
    }

    [Theory]
    [InlineData("network_manager")]
    [InlineData("org_admin")]
    [InlineData("reception")]
    public void A_tenant_scoped_role_is_not_provider_scoped(string role)
    {
        // These legitimately reach across providers; binding a sentinel for them would break the Network
        // Team's whole job.
        Principal(role, providerId: null).IsProviderScoped().Should().BeFalse();
    }

    [Fact]
    public void A_provider_scoped_principal_with_no_provider_id_is_distinguishable_from_tenant_wide()
    {
        // The two cases the binder must not conflate. "" is an explicit grant; the sentinel is a value no
        // provider_id column can equal, so the session reads nothing rather than everything.
        RlsConnectionInterceptor.NoProviderSentinel.Should().NotBeEmpty();
        RlsConnectionInterceptor.NoProviderSentinel.Should().Contain("(",
            "a parenthesis cannot appear in a UUID, so no provider row can ever carry this value");

        Principal("lab_tech", providerId: null).IsProviderScoped().Should().BeTrue();
        Principal("lab_tech", providerId: "p-1").ProviderId.Should().Be("p-1");
    }

    [Fact]
    public async Task Stamping_applies_to_every_added_row_in_one_save()
    {
        var rls = new RlsContext { TenantId = "tenant-a" };
        await using var db = Context(rls, nameof(Stamping_applies_to_every_added_row_in_one_save));

        db.Rows.AddRange(new Row { Id = 1 }, new Row { Id = 2, TenantId = "tenant-b" }, new Row { Id = 3 });
        await db.SaveChangesAsync();

        db.Rows.Select(r => r.TenantId).Should().BeEquivalentTo(["tenant-a", "tenant-b", "tenant-a"]);
    }
}
