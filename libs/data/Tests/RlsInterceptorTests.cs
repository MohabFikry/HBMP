using FluentAssertions;
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
    public async Task Nothing_is_stamped_when_no_tenant_is_bound()
    {
        // A background consumer that has not bound its tenant must NOT get an empty-string stamp that looks
        // deliberate. Leaving the value alone lets the database's NOT NULL / policy reject it loudly, which
        // is what 18.B2 made eligibility's consumer do rather than guessing a tenant.
        var rls = new RlsContext();   // TenantId = ""
        await using var db = Context(rls, nameof(Nothing_is_stamped_when_no_tenant_is_bound));

        db.Rows.Add(new Row { Id = 1, Note = "x" });
        await db.SaveChangesAsync();

        db.Rows.Single().TenantId.Should().BeEmpty();
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
