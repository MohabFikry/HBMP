using FluentAssertions;
using Mersal.Migration.Core;
using Mersal.Migration.Db;

namespace Mersal.Migration.Tests;

/// <summary>
/// Proves the two safety invariants on REAL Postgres (env-gated MIGRATION_TEST_DB, an operator
/// connection): idempotent upsert-on-natural-key (re-run updates, never duplicates) and
/// rollback-by-batch (soft-revert exactly one batch, leaving pre-existing rows intact).
/// </summary>
public sealed class PostgresSinkTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("MIGRATION_TEST_DB");

    private static Provenance Prov(Guid batch, string sourceId) => new()
    {
        SourceSystem = "test", SourceId = sourceId, BatchId = batch, LoadedAt = DateTimeOffset.UtcNow,
    };

    [SkippableFact]
    public async Task Upsert_is_idempotent_and_rollback_reverts_only_its_batch()
    {
        Skip.If(string.IsNullOrWhiteSpace(Conn), "MIGRATION_TEST_DB not set");
        var sink = new PostgresSink(Conn!);
        await sink.EnsureSchemaAsync();

        var stream = $"test-{Guid.NewGuid():N}"; // isolate this run's rows from any other
        var batch1 = Guid.NewGuid();
        var batch2 = Guid.NewGuid();

        // Insert two rows under batch1.
        (await sink.UpsertAsync(stream, "k1", "{\"v\":1}", Prov(batch1, "s1"))).Should().Be(UpsertResult.Inserted);
        (await sink.UpsertAsync(stream, "k2", "{\"v\":2}", Prov(batch1, "s2"))).Should().Be(UpsertResult.Inserted);
        (await sink.ActiveRowsAsync(stream)).Should().HaveCount(2);

        // Re-run k1 (batch2): idempotent update, not a duplicate.
        (await sink.UpsertAsync(stream, "k1", "{\"v\":99}", Prov(batch2, "s1"))).Should().Be(UpsertResult.Updated);
        var rows = await sink.ActiveRowsAsync(stream);
        rows.Should().HaveCount(2);
        rows.Single(r => r.NaturalKey == "k1").Payload.Should().Contain("99");

        // Roll back batch1 → only k2 remains active (k1 was reassigned to batch2 by the re-run).
        var reverted = await sink.RollbackBatchAsync(batch1);
        reverted.Should().Be(1);
        var afterRollback = await sink.ActiveRowsAsync(stream);
        afterRollback.Should().ContainSingle().Which.NaturalKey.Should().Be("k1");

        // Clean up this run's remaining active row.
        await sink.RollbackBatchAsync(batch2);
        (await sink.ActiveRowsAsync(stream)).Should().BeEmpty();
    }
}
