using FluentAssertions;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.3c at the datastore (env-gated <c>POLICY_TEST_DB</c>, migration 0011 applied).
///
/// <b>The acceptance criterion for the whole sub-prompt: replaying the projection produces IDENTICAL history.</b>
///
/// This is what makes "it is a projection, not a log" a claim rather than a hope. If a rebuild produced
/// different rows, the timeline would be a store of its own — derived once, then diverging quietly from the
/// audit stream it claims to reflect, and nobody would find out, because finding out requires comparing two
/// things nobody compares.
/// </summary>
public class TimelineReplayTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static TimelineProjector Projector(PolicyDbContext db) => new(db, TimeProvider.System);

    private static string Tenant() => $"tl-test-{Guid.NewGuid():N}";

    /// <summary>A member's history: enrolled, plan changed, a clinical note, a document, terminated. The five
    /// events the acceptance criterion names.</summary>
    private static List<TimelineSource> Story(Guid memberRef)
    {
        var actor = Guid.NewGuid();
        DateTimeOffset at(int day) => new(2026, 3, day, 9, 0, 0, TimeSpan.Zero);
        return
        [
            new(Guid.NewGuid(), "MemberEnrolled", NoteScope.Member, memberRef, at(1), "policy-service",
                actor, "officer.mona", "Mona Adel"),
            new(Guid.NewGuid(), "MemberPlanChanged", NoteScope.Member, memberRef, at(5), "policy-service",
                actor, "officer.mona", "Mona Adel",
                Changes: new Dictionary<string, (string?, string?)> { ["plan"] = ("Standard", "Oncology") }),
            new(Guid.NewGuid(), "NoteAdded", NoteScope.Member, memberRef, at(9), "policy-service",
                actor, "dr.hoda", "Dr Hoda Saleh", VisibilityClass: NoteVisibility.Clinical,
                Changes: new Dictionary<string, (string?, string?)> { ["body"] = (null, "declined referral") }),
            new(Guid.NewGuid(), "DocumentAttached", NoteScope.Member, memberRef, at(12), "policy-service",
                actor, "officer.mona", "Mona Adel"),
            new(Guid.NewGuid(), "MemberTerminated", NoteScope.Member, memberRef, at(20), "policy-service",
                actor, "supervisor.amal", "Amal Nabil",
                Changes: new Dictionary<string, (string?, string?)> { ["status"] = ("Active", "Terminated") }),
        ];
    }

    private static async Task Cleanup(string tenant)
    {
        await using var db = Ctx();
        // Same transaction scoping the projector's rebuild uses — SET LOCAL outside a transaction is a no-op.
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL app.timeline_rebuild = 'on'");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.entity_timeline WHERE tenant_id = {0}", tenant);
        await tx.CommitAsync();
    }

    // ---- The five-event story, in order ------------------------------------------------------------------

    [SkippableFact]
    public async Task A_members_history_reads_in_chronological_order_with_the_acting_username()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var member = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var written = await Projector(db).ProjectAsync(Story(member), tenant);
            written.Should().Be(5);

            var entries = await db.TimelineEntries.AsNoTracking()
                .Where(e => e.ScopeRef == member).OrderBy(e => e.OccurredAt).ToListAsync();

            entries.Select(e => e.EventType).Should().Equal(
                "MemberEnrolled", "MemberPlanChanged", "NoteAdded", "DocumentAttached", "MemberTerminated");
            entries[2].ActorUsername.Should().Be("dr.hoda", "the actor is a snapshot, per entry");
            entries[4].ActorUsername.Should().Be("supervisor.amal");
            entries.Should().OnlyContain(e => e.OccurredAt.Offset == TimeSpan.Zero, "stored UTC");
        }
        finally { await Cleanup(tenant); }
    }

    // ---- Idempotency -------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Re_delivering_the_same_events_adds_nothing()
    {
        // At-least-once delivery makes re-delivery NORMAL, not exceptional. A duplicated line in someone's
        // history is not a cosmetic defect — it reads as the same thing having happened twice.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var member = Guid.NewGuid();
        var story = Story(member);
        try
        {
            await using var db = Ctx();
            (await Projector(db).ProjectAsync(story, tenant)).Should().Be(5);
            (await Projector(db).ProjectAsync(story, tenant)).Should().Be(0, "every event was already projected");

            (await db.TimelineEntries.AsNoTracking().CountAsync(e => e.ScopeRef == member)).Should().Be(5);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_duplicate_within_one_batch_is_collapsed()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var member = Guid.NewGuid();
        var story = Story(member);
        try
        {
            await using var db = Ctx();
            // The same event arriving twice in one delivery — a redelivery that overlaps a batch boundary.
            var withDuplicate = story.Concat([story[0]]).ToList();

            (await Projector(db).ProjectAsync(withDuplicate, tenant)).Should().Be(5);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task The_unique_index_is_the_backstop_under_the_idempotency_check()
    {
        // The in-code check is the fast path; this is what holds when two projector instances race.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var member = Guid.NewGuid();
        var source = Story(member)[0];
        try
        {
            await using (var db = Ctx())
                await Projector(db).ProjectAsync([source], tenant);

            await using var second = Ctx();
            // Bypass the idempotency check entirely, as a racing instance effectively would.
            second.TimelineEntries.Add(
                TimelineProjection.Project(source with { }, tenant, DateTimeOffset.UtcNow));

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("23505");
        }
        finally { await Cleanup(tenant); }
    }

    // ---- THE replay guarantee ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_rebuild_produces_byte_identical_history()
    {
        // THE acceptance criterion. Not "equivalent" — identical, ids included, so the rebuild can be VERIFIED
        // by comparison rather than eyeballed.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var member = Guid.NewGuid();
        var story = Story(member);
        try
        {
            List<TimelineEntry> before;
            await using (var db = Ctx())
            {
                await Projector(db).ProjectAsync(story, tenant);
                before = await db.TimelineEntries.AsNoTracking()
                    .Where(e => e.TenantId == tenant).OrderBy(e => e.EntryId).ToListAsync();
            }

            await using (var db = Ctx())
            {
                var rebuilt = await Projector(db).RebuildAsync(story, tenant);
                rebuilt.Should().Be(5, "the rebuild re-derives every entry from source");
            }

            await using var check = Ctx();
            var after = await check.TimelineEntries.AsNoTracking()
                .Where(e => e.TenantId == tenant).OrderBy(e => e.EntryId).ToListAsync();

            after.Should().HaveCount(before.Count);
            for (var i = 0; i < before.Count; i++)
            {
                after[i].EntryId.Should().Be(before[i].EntryId, "the id is derived from the source event id");
                after[i].SourceEventId.Should().Be(before[i].SourceEventId);
                after[i].EventType.Should().Be(before[i].EventType);
                after[i].EventCategory.Should().Be(before[i].EventCategory);
                after[i].OccurredAt.Should().Be(before[i].OccurredAt);
                after[i].ActorUsername.Should().Be(before[i].ActorUsername);
                after[i].SummaryEn.Should().Be(before[i].SummaryEn);
                after[i].SummaryAr.Should().Be(before[i].SummaryAr);
                after[i].ChangeDiff.Should().Be(before[i].ChangeDiff, "the diff serializer orders its keys");
                after[i].VisibilityClass.Should().Be(before[i].VisibilityClass);
            }
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_rebuild_repairs_a_gap_without_duplicating_what_survived()
    {
        // The realistic reason to rebuild: a projection that fell behind or dropped an event. It must fill the
        // hole and leave everything else exactly as it was.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var member = Guid.NewGuid();
        var story = Story(member);
        try
        {
            await using (var db = Ctx())
                await Projector(db).ProjectAsync(story.Take(3).ToList(), tenant);   // three of five arrived

            await using (var db = Ctx())
                (await Projector(db).RebuildAsync(story, tenant)).Should().Be(5);

            await using var check = Ctx();
            (await check.TimelineEntries.AsNoTracking().CountAsync(e => e.TenantId == tenant)).Should().Be(5);
        }
        finally { await Cleanup(tenant); }
    }

    // ---- Append-only, enforced ---------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_entry_can_never_be_edited()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var member = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            await Projector(db).ProjectAsync([Story(member)[0]], tenant);

            var entry = await db.TimelineEntries.FirstAsync(e => e.TenantId == tenant);
            entry.SummaryEn = "Something else entirely";

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("immutable");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_single_inconvenient_entry_cannot_be_deleted_outside_a_rebuild()
    {
        // The asymmetry that makes the rebuild path safe: discarding ALL derived data is fine; quietly
        // removing one line from someone's history is not.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var member = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            await Projector(db).ProjectAsync([Story(member)[0]], tenant);

            var entry = await db.TimelineEntries.FirstAsync(e => e.TenantId == tenant);
            db.TimelineEntries.Remove(entry);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("append-only");
        }
        finally { await Cleanup(tenant); }
    }
}
