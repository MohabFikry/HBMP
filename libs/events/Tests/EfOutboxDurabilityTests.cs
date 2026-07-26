using System.Data;
using FluentAssertions;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Mersal.Events.Tests;

/// <summary>
/// Durability proof for the EF-backed transactional outbox (16.2 / C1). Env-gated on
/// <c>EVENTS_TEST_DB</c> (a Postgres connection string) so it runs where a DB is available and SKIPS
/// (early-return) otherwise, matching the platform's DB-integration test convention. Proves the row is
/// in Postgres — not a process-local queue — so it survives the DbContext/process that enqueued it, and
/// that poison messages are quarantined after MaxAttempts.
/// </summary>
public class EfOutboxDurabilityTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("EVENTS_TEST_DB");
    private const string Schema = "events_outbox_test";

    private sealed class TestOutboxContext(DbContextOptions<TestOutboxContext> o) : DbContext(o)
    {
        protected override void OnModelCreating(ModelBuilder b) => b.AddOutbox(Schema);
    }

    private static TestOutboxContext NewContext()
    {
        var opt = new DbContextOptionsBuilder<TestOutboxContext>().UseNpgsql(Db!).Options;
        return new TestOutboxContext(opt);
    }

    private static async Task ResetSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(Db!);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{Schema}\";" + OutboxSchema.Ddl(Schema) +
                          $"TRUNCATE \"{Schema}\".outbox_message;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static EfOutboxReader Reader(DbContext db, int maxAttempts = 8) =>
        new(db, Options.Create(new EventsOptions { MaxAttempts = maxAttempts }));

    [SkippableFact]
    public async Task Enqueued_row_is_durable_and_claimed_by_a_fresh_context_then_drains()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await ResetSchemaAsync();

        // Enqueue through context A, then dispose it entirely (simulates the request/process ending).
        Guid id;
        await using (var write = NewContext())
        {
            var outbox = new EfOutbox(write);
            await outbox.EnqueueAsync("OrderLineConsumed", "orders.events", new { orderId = "ORD-1", qty = 2 });
        }

        // A brand-new context (fresh connection) still sees the row → it is in Postgres, not in-process.
        await using var relay = NewContext();
        var reader = Reader(relay);
        var batch = await reader.DequeueBatchAsync(10);
        batch.Should().ContainSingle();
        id = batch[0].EventId;
        batch[0].EventType.Should().Be("OrderLineConsumed");
        batch[0].Payload.Should().Contain("ORD-1");
        batch[0].Attempts.Should().Be(1); // claim increments attempts atomically

        await reader.MarkProcessedAsync(id);
        (await reader.DequeueBatchAsync(10)).Should().BeEmpty(); // drained
    }

    [SkippableFact]
    public async Task Broker_down_leaves_row_pending_then_poison_is_quarantined_after_max_attempts()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await ResetSchemaAsync();

        await using var db = NewContext();
        await new EfOutbox(db).EnqueueAsync("A", "q", new { x = 1 });
        var reader = Reader(db, maxAttempts: 3);

        // Each pass claims (attempts++) then "publish fails" → MarkFailed leaves processed_at NULL.
        for (var pass = 1; pass <= 3; pass++)
        {
            var batch = await reader.DequeueBatchAsync(10);
            batch.Should().ContainSingle($"attempt {pass} is below the cap");
            await reader.MarkFailedAsync(batch[0].EventId, "broker down");
        }

        // attempts has now reached the cap → the poison row is quarantined (skipped), not looping forever.
        (await reader.DequeueBatchAsync(10)).Should().BeEmpty("the poison row is past MaxAttempts");
    }
}
