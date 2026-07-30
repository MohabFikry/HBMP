using FluentAssertions;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 24 Gate 3 — INV-DEDUPE-SURVIVES-RESTART.
///
/// <para>"Consumers are idempotent (dedupe on event id)" is a claim about a process that has DIED. The
/// broker delivers at least once, so a redelivery after a crash is the normal case, not the exotic one —
/// and the interesting question is never "does the second delivery get rejected" but "does it get rejected
/// by a process that has no memory of the first".</para>
///
/// <para><see cref="InMemoryProcessedEventStore"/> answers with a <c>ConcurrentDictionary</c>: correct for
/// the life of one process and empty the instant it restarts. It is still the DEFAULT registration in
/// <c>AddHbmpEvents</c> — directly above a comment reading "Default = durable. In-memory only when
/// explicitly opted in (dev/test), never in production", which is true of the OUTBOX on the lines below it
/// and not of the dedupe store on the line above. identity-service is the one service that overrides it.</para>
///
/// <para>So this proves the durable store the way the invariant is worded: a SECOND store instance, built on
/// a fresh DbContext, must refuse an id the first one claimed. A test that reuses one instance proves only
/// that a dictionary works. Env-gated on <c>IDENTITY_TEST_DB</c>; self-cleans by event id.</para>
/// </summary>
public class ProcessedEventDurabilityTests
{
    private static readonly string? Db =
        Environment.GetEnvironmentVariable("IDENTITY_TEST_DB")
        ?? Environment.GetEnvironmentVariable("IDENTITY_TEST_DB_OWNER");

    private static IdentityStoreDbContext Ctx() => new(new DbContextOptionsBuilder<IdentityStoreDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    [SkippableFact]
    public async Task A_redelivered_event_is_refused_by_a_process_that_never_saw_the_first_delivery()
    {
        Skip.If(Db is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var eventId = Guid.NewGuid();
        try
        {
            // Process A claims the event and then dies — context disposed, nothing carried over.
            await using (var first = Ctx())
                (await new DbProcessedEventStore(first).TryBeginAsync(eventId)).Should().BeTrue(
                    "the first delivery must be allowed through exactly once");

            // Process B is a fresh start with no memory of A. This is the assertion the invariant is about.
            await using (var second = Ctx())
                (await new DbProcessedEventStore(second).TryBeginAsync(eventId)).Should().BeFalse(
                    "an at-least-once broker redelivers after a crash, and a consumer that forgets on restart " +
                    "applies the event twice — a second enrolment, a second dispense, a second decision");
        }
        finally { await CleanupAsync(eventId); }
    }

    /// <summary>Two consumers racing the SAME redelivery: exactly one may proceed. A select-then-insert
    /// store lets both through, which is why the implementation claims with ON CONFLICT DO NOTHING.</summary>
    [SkippableFact]
    public async Task Two_consumers_racing_one_redelivery_yield_exactly_one_winner()
    {
        Skip.If(Db is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var eventId = Guid.NewGuid();
        try
        {
            await using var a = Ctx();
            await using var b = Ctx();
            var results = await Task.WhenAll(
                new DbProcessedEventStore(a).TryBeginAsync(eventId),
                new DbProcessedEventStore(b).TryBeginAsync(eventId));

            results.Count(r => r).Should().Be(1,
                "exactly one claim may win; two winners means the handler runs twice and no winner means " +
                "the event is dropped");
        }
        finally { await CleanupAsync(eventId); }
    }

    /// <summary>A distinct id is not blocked by an unrelated one — otherwise "always false" would pass
    /// the tests above and stop the platform processing anything.</summary>
    [SkippableFact]
    public async Task An_unrelated_event_is_still_allowed_through()
    {
        Skip.If(Db is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var store = new DbProcessedEventStore(db);
            (await store.TryBeginAsync(first)).Should().BeTrue();
            (await store.TryBeginAsync(second)).Should().BeTrue("a different event id is a different event");
        }
        finally { await CleanupAsync(first); await CleanupAsync(second); }
    }

    private static async Task CleanupAsync(Guid eventId)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM identity.processed_event WHERE event_id = {0}", eventId);
    }
}
