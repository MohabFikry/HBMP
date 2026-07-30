using System.Data;
using FluentAssertions;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

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

    // ---- a stand-in for "the state change" -----------------------------------------------------------
    // A real table in the same schema, written through the same DbContext, so the atomicity assertions are
    // about one Postgres transaction rather than about EF's change tracker.

    private static async Task ResetBusinessTableAsync()
    {
        await using var conn = new NpgsqlConnection(Db!);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"CREATE TABLE IF NOT EXISTS \"{Schema}\".business_row (id text PRIMARY KEY);" +
            $"TRUNCATE \"{Schema}\".business_row;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task WriteBusinessRowAsync(DbContext db, string id) =>
        db.Database.ExecuteSqlRawAsync($"INSERT INTO \"{Schema}\".business_row (id) VALUES ({{0}})", id);

    private static async Task<int> BusinessRowCountAsync(string id)
    {
        await using var conn = new NpgsqlConnection(Db!);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM \"{Schema}\".business_row WHERE id = @id";
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Text) { Value = id });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

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

    /// <summary>
    /// INV-OUTBOX-SURVIVES-CRASH, stated precisely: the outbox row and the state change it announces must
    /// commit or roll back TOGETHER.
    ///
    /// <para>"The row is in Postgres" (the test above) is a weaker claim than the invariant, and the gap
    /// between them is where events are lost. <see cref="EfOutbox.EnqueueRawAsync"/> calls its own
    /// <c>SaveChangesAsync</c>, so a handler that commits its business change and THEN enqueues has two
    /// separate commits with a window between them: a process kill in that window leaves the state changed
    /// and the event gone forever, and no retry will produce it because nothing records that it was owed.
    /// Enqueueing first is not better — it publishes an event for a state change that may never commit.</para>
    ///
    /// <para>One transaction around both is what closes it, and that is what this proves: rollback must take
    /// the event with it, commit must keep both.
    /// <c>OutboxAtomicityTests</c> in libs/architecture asserts that handlers actually do this.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_state_change_and_its_event_commit_or_roll_back_together()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await ResetSchemaAsync();
        await ResetBusinessTableAsync();

        // ---- rollback: the state change is abandoned, so its event must not survive it.
        await using (var db = NewContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await WriteBusinessRowAsync(db, "ORD-ROLLED-BACK");
            await new EfOutbox(db).EnqueueAsync("OrderLineConsumed", "orders.events", new { orderId = "ORD-ROLLED-BACK" });
            await tx.RollbackAsync();
        }

        (await BusinessRowCountAsync("ORD-ROLLED-BACK")).Should().Be(0, "the transaction rolled back");
        await using (var check = NewContext())
        {
            (await Reader(check).DequeueBatchAsync(10)).Should().BeEmpty(
                "an event announcing a state change that never happened is a phantom: consumers would act " +
                "on an order line nobody consumed, and no later retry can un-send it");
        }

        // ---- commit: both survive, and the relay can still claim the event from a fresh connection.
        await using (var db = NewContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await WriteBusinessRowAsync(db, "ORD-COMMITTED");
            await new EfOutbox(db).EnqueueAsync("OrderLineConsumed", "orders.events", new { orderId = "ORD-COMMITTED" });
            await tx.CommitAsync();
        }

        (await BusinessRowCountAsync("ORD-COMMITTED")).Should().Be(1);
        await using (var relay = NewContext())
        {
            var batch = await Reader(relay).DequeueBatchAsync(10);
            batch.Should().ContainSingle("the committed state change owes exactly one event");
            batch[0].Payload.Should().Contain("ORD-COMMITTED");
        }
    }

    /// <summary>
    /// The failure this invariant exists to prevent, demonstrated rather than described: commit the business
    /// change, then die before the enqueue. The state is durable and the event is gone — and nothing anywhere
    /// records that it was owed, so no relay, retry or replay will ever produce it. This is what every handler
    /// that saves and then enqueues outside a transaction is exposed to.
    /// </summary>
    [SkippableFact]
    public async Task Committing_the_state_change_before_enqueueing_loses_the_event_on_a_crash()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await ResetSchemaAsync();
        await ResetBusinessTableAsync();

        await using (var db = NewContext())
        {
            await WriteBusinessRowAsync(db, "ORD-LOST");     // business commit #1
            // ...process killed here. The enqueue that would have been commit #2 never runs.
        }

        (await BusinessRowCountAsync("ORD-LOST")).Should().Be(1, "the state change is durable");
        await using var check = NewContext();
        (await Reader(check).DequeueBatchAsync(10)).Should().BeEmpty(
            "and the event is unrecoverable — this is the loss the single-transaction rule prevents, and " +
            "the reason 'the outbox row is in Postgres' is not the same guarantee as 'the event cannot be lost'");
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
