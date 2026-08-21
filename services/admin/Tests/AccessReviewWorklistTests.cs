using FluentAssertions;
using Mersal.Admin.Api;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Tests;

/// <summary>
/// The access-review WORKLIST (33.7) — the read that made recertify and revoke reachable, and the tenant
/// boundary that read exposed.
/// </summary>
/// <remarks>
/// <para><c>AdminIntegrationTests</c> already proves the campaign lifecycle end to end: a T3 campaign
/// snapshots the right grants, a recertified one survives the deadline and an untouched one is auto-expired.
/// Every one of those assertions handed <c>RecertifyAsync</c> an <c>ItemId</c> read straight out of the
/// campaign object it had just created. No test was ever a caller, and no caller could exist — nothing on any
/// service returned an item id — so the surface passed while being unusable, and the query underneath it went
/// unexamined for the same reason.</para>
///
/// <para>The tenant test below is the one that matters. <c>admin.access_review_item</c> carries no
/// <c>tenant_id</c> and has no RLS policy; migration 0005 lists it under "deliberately NOT tenant-isolated"
/// on the ground that its rows are "reached only through their campaign, which IS isolated". That reasoning
/// is sound and <c>DecideAsync</c> did not honour it — it looked the item up by id alone.</para>
/// </remarks>
[Collection("admin-db")]
public class AccessReviewWorklistTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");

    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static (RoleAdminService, AccessReviewService) Build(AdminDbContext db, TimeProvider clock)
    {
        var audit = new AuditClient(new InMemoryAuditOutbox(), new AuditClientContext("admin-test"), clock);
        return (new RoleAdminService(db, audit, clock, new TenantProgramStore(db)), new AccessReviewService(db, audit, clock));
    }

    private static ActorContext Actor(string tenant) => new("admin-1", "org_admin", tenant, Mfa: true);

    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N")[..10];
    private static string NewSubject() => "u-" + Guid.NewGuid().ToString("N")[..10];

    [SkippableFact]
    public async Task The_worklist_lists_every_grant_a_campaign_is_reviewing()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = NewTenant();
        var alice = NewSubject();
        var bilal = NewSubject();
        try
        {
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            await using var db = Ctx();
            var (roles, review) = Build(db, clock);

            await roles.GrantAsync(Actor(tenant), tenant, alice, "doctor", ScopeType.Tenant, null, "treating");
            await roles.GrantAsync(Actor(tenant), tenant, bilal, "medical_approval", ScopeType.Tenant, null, "approvals");
            var campaign = await review.CreateCampaignAsync(
                Actor(tenant), tenant, "Q3 review", SensitivityTier.T3, clock.GetUtcNow().AddDays(14));

            var items = await review.ItemsAsync(tenant, campaign.CampaignId);

            items.Should().HaveCount(2);
            items.Select(i => i.SubjectUserId).Should().BeEquivalentTo([alice, bilal]);
            items.Should().OnlyContain(i => i.Decision == "Pending");
            // The binding id travels with the row: revoking an item revokes a BINDING, and an audit reader
            // asking "which grant did this remove?" should not have to join back through the campaign.
            items.Should().OnlyContain(i => i.BindingId != Guid.Empty);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task The_worklist_puts_the_outstanding_grants_first()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = NewTenant();
        try
        {
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            await using var db = Ctx();
            var (roles, review) = Build(db, clock);

            // Three grants; two get decided, one is left alone. Whatever the decision order, the outstanding
            // one has to surface at the top: this list is opened to finish the work, not to read the history.
            var subjects = new[] { NewSubject(), NewSubject(), NewSubject() };
            foreach (var s in subjects)
                await roles.GrantAsync(Actor(tenant), tenant, s, "doctor", ScopeType.Tenant, null, "treating");

            var campaign = await review.CreateCampaignAsync(
                Actor(tenant), tenant, "Q3 review", SensitivityTier.T3, clock.GetUtcNow().AddDays(14));

            var all = await review.ItemsAsync(tenant, campaign.CampaignId);
            await review.RecertifyAsync(Actor(tenant), tenant, all[0].ItemId, "still treating");
            await review.ReviewRevokeAsync(Actor(tenant), tenant, all[1].ItemId, "left the service");

            var after = await review.ItemsAsync(tenant, campaign.CampaignId);
            after[0].Decision.Should().Be("Pending");
            after.Skip(1).Should().OnlyContain(i => i.Decision != "Pending");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_decided_grant_records_who_decided_it_when_and_why()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = NewTenant();
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.Zero));
            await using var db = Ctx();
            var (roles, review) = Build(db, clock);

            await roles.GrantAsync(Actor(tenant), tenant, NewSubject(), "doctor", ScopeType.Tenant, null, "treating");
            var campaign = await review.CreateCampaignAsync(
                Actor(tenant), tenant, "Q3 review", SensitivityTier.T3, clock.GetUtcNow().AddDays(14));
            var item = (await review.ItemsAsync(tenant, campaign.CampaignId)).Single();

            await review.RecertifyAsync(Actor(tenant), tenant, item.ItemId, "confirmed with the clinical lead");

            // The note is the whole evidentiary value of a recertification — "somebody said yes" is not a
            // record of a review, and this projection is what an auditor reads back.
            var decided = (await review.ItemsAsync(tenant, campaign.CampaignId)).Single();
            decided.Decision.Should().Be("Recertified");
            decided.DecidedBy.Should().Be("admin-1");
            decided.DecidedAt.Should().Be(clock.GetUtcNow());
            decided.Note.Should().Be("confirmed with the clinical lead");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Another_tenants_campaign_shows_no_items_rather_than_its_own()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var mine = NewTenant();
        var theirs = NewTenant();
        try
        {
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            await using var db = Ctx();
            var (roles, review) = Build(db, clock);

            await roles.GrantAsync(Actor(theirs), theirs, NewSubject(), "doctor", ScopeType.Tenant, null, "treating");
            var theirCampaign = await review.CreateCampaignAsync(
                Actor(theirs), theirs, "Their review", SensitivityTier.T3, clock.GetUtcNow().AddDays(14));

            (await review.ItemsAsync(theirs, theirCampaign.CampaignId)).Should().HaveCount(1);
            (await review.ItemsAsync(mine, theirCampaign.CampaignId)).Should()
                .BeEmpty("a campaign id from another tenant is not a key into this one's grants");
        }
        finally { await Cleanup(mine); await Cleanup(theirs); }
    }

    /// <summary>
    /// The bite. Before 33.7 this passed: <c>DecideAsync</c> found the item by id alone, so a neighbouring
    /// tenant's administrator could mark a grant Recertified — which is exactly what stops the sweep removing
    /// it at the deadline. The binding itself was never at risk (<c>role_binding</c> IS RLS-isolated, so the
    /// revoke half found nothing), and that is what made this quiet: the write that landed was the one nobody
    /// was watching.
    /// </summary>
    [SkippableFact]
    public async Task One_tenant_cannot_recertify_another_tenants_grant_and_defeat_its_review()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var attacker = NewTenant();
        var victim = NewTenant();
        var subject = NewSubject();
        try
        {
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            await using var db = Ctx();
            var (roles, review) = Build(db, clock);

            await roles.GrantAsync(Actor(victim), victim, subject, "doctor", ScopeType.Tenant, null, "treating");
            var campaign = await review.CreateCampaignAsync(
                Actor(victim), victim, "Q3 review", SensitivityTier.T3, clock.GetUtcNow().AddDays(14));
            var item = (await review.ItemsAsync(victim, campaign.CampaignId)).Single();

            // Holding the item id and acting as their own tenant's administrator.
            (await review.RecertifyAsync(Actor(attacker), attacker, item.ItemId, "not mine to keep"))
                .Should().BeFalse("the item belongs to a campaign in another tenant");
            (await review.ReviewRevokeAsync(Actor(attacker), attacker, item.ItemId, "nor mine to remove"))
                .Should().BeFalse();

            // Untouched, and therefore still swept at the deadline — which is the consequence that matters.
            (await review.ItemsAsync(victim, campaign.CampaignId)).Single().Decision.Should().Be("Pending");
            clock.Advance(TimeSpan.FromDays(15));
            (await review.SweepExpiredAsync(Actor(victim), victim, campaign.CampaignId)).Should().Be(1);
            (await roles.EffectiveRolesAsync(victim, subject)).Should().BeEmpty();
        }
        finally { await Cleanup(attacker); await Cleanup(victim); }
    }

    private static async Task Cleanup(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        var campaignIds = await db.Campaigns.Where(c => c.TenantId == tenant).Select(c => c.CampaignId).ToListAsync();
        await db.ReviewItems.Where(i => campaignIds.Contains(i.CampaignId)).ExecuteDeleteAsync();
        await db.Campaigns.Where(c => c.TenantId == tenant).ExecuteDeleteAsync();
        await db.RoleBindings.Where(b => b.TenantId == tenant).ExecuteDeleteAsync();
    }
}

/// <summary>
/// The governance token (33.7) — the shortening that used to happen in the browser.
/// </summary>
/// <remarks>
/// No DB and no HTTP: the whole point of moving this is that the full subject id must not leave the service,
/// which is a property of one pure function. Tested here so a future change that "simplifies" it back to
/// returning the id has something to break.
/// </remarks>
public class GovernanceTokenTests
{
    [Fact]
    public void A_token_reveals_only_the_last_four_characters_of_a_subject_id()
    {
        var token = GovernanceToken.Of("8f14e45f-ceea-467a-9c4f-1e2a3b4c5d91");

        token.Should().Be("•••5d91");
        token.Should().NotContain("8f14e45f", "the point is that the identifier does not travel");
    }

    [Fact]
    public void A_subject_with_no_id_gets_no_token_rather_than_an_empty_looking_one()
    {
        // Null means "nobody has approved this yet". A token there would read as somebody having done so.
        GovernanceToken.Of(null).Should().BeNull();
        GovernanceToken.Of("   ").Should().BeNull();
    }

    [Fact]
    public void A_short_id_is_not_padded_into_looking_longer_than_it_is()
    {
        GovernanceToken.Of("ab").Should().Be("•••ab");
    }
}
