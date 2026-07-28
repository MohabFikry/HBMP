using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Data.Tests;

/// <summary>
/// 21.5 — AMBIENT ATTRIBUTION (design 40 §6, ADR-0021).
///
/// Every write records WHICH MEMBERSHIP made it, without the handler doing anything. That placement is the
/// point: an attribution gap is created by OMISSION, and the endpoint that forgets is the one nobody wrote
/// a test for. The missing name then only becomes visible during the incident review that needed it.
///
/// The membership rather than the raw user id, because one person may act in two organisations —
/// "u-1234 changed this" cannot say which hat they were wearing, and in a benefit platform that is the
/// difference between a clinician acting for Mersal and the same clinician acting for a partner NGO.
/// </summary>
public class MembershipAttributionTests
{
    private sealed class Attributed
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public string UpdatedBy { get; set; } = "";
        public string Note { get; set; } = "";
    }

    /// <summary>A table with no attribution columns at all — the stamper must simply leave it alone.</summary>
    private sealed class Plain
    {
        public int Id { get; set; }
        public string Note { get; set; } = "";
    }

    private sealed class TestDb(DbContextOptions<TestDb> o) : DbContext(o)
    {
        public DbSet<Attributed> Rows => Set<Attributed>();
        public DbSet<Plain> Plains => Set<Plain>();
    }

    private static TestDb Context(RlsContext rls, string name) =>
        new(new DbContextOptionsBuilder<TestDb>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(new TenantStampingInterceptor(rls))
            .Options);

    [Fact]
    public async Task THE_acceptance_case_a_write_records_the_membership_without_the_handler_doing_it()
    {
        var rls = new RlsContext { TenantId = "t-1", MembershipId = "m-42" };
        await using var db = Context(rls, nameof(THE_acceptance_case_a_write_records_the_membership_without_the_handler_doing_it));

        // Note what the "handler" sets: nothing but the payload.
        db.Rows.Add(new Attributed { Id = 1, Note = "clinical note" });
        await db.SaveChangesAsync();

        var row = db.Rows.Single();
        row.CreatedBy.Should().Be("m-42");
        row.UpdatedBy.Should().Be("m-42");
        row.TenantId.Should().Be("t-1");
    }

    [Fact]
    public async Task An_update_rewrites_updated_by_but_preserves_created_by()
    {
        var author = new RlsContext { TenantId = "t-1", MembershipId = "m-author" };
        var name = nameof(An_update_rewrites_updated_by_but_preserves_created_by);

        await using (var db = Context(author, name))
        {
            db.Rows.Add(new Attributed { Id = 1, Note = "first" });
            await db.SaveChangesAsync();
        }

        var editor = new RlsContext { TenantId = "t-1", MembershipId = "m-editor" };
        await using (var db = Context(editor, name))
        {
            var row = db.Rows.Single();
            row.Note = "amended";
            await db.SaveChangesAsync();
        }

        await using var check = Context(author, name);
        var final = check.Rows.Single();
        final.CreatedBy.Should().Be("m-author", "who wrote it originally is a permanent fact");
        final.UpdatedBy.Should().Be("m-editor",
            "updated_by names whoever made THIS change — carrying the author forward would misattribute " +
            "every subsequent edit to the wrong person");
    }

    [Fact]
    public async Task An_explicit_created_by_is_not_overwritten()
    {
        // Mirrors the tenant rule: the stamper fills a BLANK. An import or a backfill that sets attribution
        // deliberately must keep it, or the migration's own identity replaces the real author.
        var rls = new RlsContext { TenantId = "t-1", MembershipId = "m-42" };
        await using var db = Context(rls, nameof(An_explicit_created_by_is_not_overwritten));

        db.Rows.Add(new Attributed { Id = 1, CreatedBy = "migration:0007", Note = "imported" });
        await db.SaveChangesAsync();

        db.Rows.Single().CreatedBy.Should().Be("migration:0007");
    }

    [Fact]
    public async Task Nothing_is_stamped_for_a_principal_with_no_membership()
    {
        // Machine principals (client-credentials) legitimately have none. An empty-string stamp would look
        // like a real attribution to anyone reading the table, which is worse than an obvious blank.
        var rls = new RlsContext { TenantId = "t-1", MembershipId = "" };
        await using var db = Context(rls, nameof(Nothing_is_stamped_for_a_principal_with_no_membership));

        db.Rows.Add(new Attributed { Id = 1, Note = "by a worker" });
        await db.SaveChangesAsync();

        db.Rows.Single().CreatedBy.Should().BeEmpty();
    }

    [Fact]
    public async Task A_table_without_attribution_columns_is_untouched()
    {
        var rls = new RlsContext { TenantId = "t-1", MembershipId = "m-42" };
        await using var db = Context(rls, nameof(A_table_without_attribution_columns_is_untouched));

        db.Plains.Add(new Plain { Id = 1, Note = "no attribution columns here" });
        var act = async () => await db.SaveChangesAsync();

        await act.Should().NotThrowAsync("the stamper must skip entities that have no such property");
    }

    [Fact]
    public async Task Attribution_and_tenant_stamping_are_independent()
    {
        // A service that binds a membership but no tenant (or vice versa) must still get the half it has,
        // rather than losing both because one guard short-circuited the whole method.
        var rls = new RlsContext { TenantId = "", MembershipId = "m-42" };
        await using var db = Context(rls, nameof(Attribution_and_tenant_stamping_are_independent));

        db.Rows.Add(new Attributed { Id = 1, Note = "x" });
        await db.SaveChangesAsync();

        var row = db.Rows.Single();
        row.CreatedBy.Should().Be("m-42");
        row.TenantId.Should().BeEmpty();
    }
}
