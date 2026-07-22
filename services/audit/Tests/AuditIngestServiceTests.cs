using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Audit.Domain;

namespace Mersal.Audit.Tests;

public class AuditIngestServiceTests
{
    // --- in-memory fakes (no DB / Docker needed) ---
    private sealed class FakeStore : IAuditEventStore
    {
        public List<AuditEvent> Appended { get; } = [];
        public Task<string?> GetLastRecordHashAsync(string partitionKey, CancellationToken ct = default) =>
            Task.FromResult(Appended.LastOrDefault(x => AuditPartition.KeyFor(x.OccurredAt) == partitionKey)?.RecordHash);
        public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Appended.Any(x => x.AuditEventId == id));
        public Task AppendAsync(AuditEvent chained, CancellationToken ct = default) { Appended.Add(chained); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditEvent>> ReadPartitionAsync(string partitionKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEvent>>(
                Appended.Where(x => AuditPartition.KeyFor(x.OccurredAt) == partitionKey).ToList());
    }

    private sealed class FakeWorm : IWormStore
    {
        public int Calls { get; private set; }
        public Task PersistAsync(AuditEvent chained, CancellationToken ct = default) { Calls++; return Task.CompletedTask; }
    }

    private sealed class FakeAlerter : IIntegrityAlerter
    {
        public List<ChainVerification> Alerts { get; } = [];
        public Task RaiseAsync(string p, ChainVerification r, CancellationToken ct = default) { Alerts.Add(r); return Task.CompletedTask; }
    }

    private static AuditEvent Raw(Guid id, string entityId) => new()
    {
        AuditEventId = id, ServiceName = "patient-service", SourceService = "patient-service",
        EntityType = "beneficiary", EntityId = entityId, Action = AuditAction.Create,
        OccurredAt = new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Ingest_appends_chained_record_and_writes_worm()
    {
        var store = new FakeStore(); var worm = new FakeWorm();
        var ingest = new AuditIngestService(store, worm);

        var r1 = await ingest.IngestAsync(Raw(Guid.NewGuid(), "A"));
        var r2 = await ingest.IngestAsync(Raw(Guid.NewGuid(), "B"));

        r1.Should().Be(IngestResult.Appended);
        r2.Should().Be(IngestResult.Appended);
        store.Appended.Should().HaveCount(2);
        store.Appended[0].PrevHash.Should().Be(HashChain.Genesis);
        store.Appended[1].PrevHash.Should().Be(store.Appended[0].RecordHash); // chained
        worm.Calls.Should().Be(2);
        HashChain.Verify(store.Appended).IsIntact.Should().BeTrue();
    }

    [Fact]
    public async Task Duplicate_delivery_is_idempotent_no_double_append()
    {
        var store = new FakeStore(); var worm = new FakeWorm();
        var ingest = new AuditIngestService(store, worm);
        var id = Guid.NewGuid();

        var first = await ingest.IngestAsync(Raw(id, "A"));
        var replay = await ingest.IngestAsync(Raw(id, "A")); // same id (at-least-once redelivery)

        first.Should().Be(IngestResult.Appended);
        replay.Should().Be(IngestResult.Duplicate);
        store.Appended.Should().HaveCount(1);
        worm.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Verifier_detects_tamper_and_raises_critical_alert()
    {
        var store = new FakeStore(); var worm = new FakeWorm();
        var ingest = new AuditIngestService(store, worm);
        await ingest.IngestAsync(Raw(Guid.NewGuid(), "A"));
        await ingest.IngestAsync(Raw(Guid.NewGuid(), "B"));

        // Simulate an attacker editing a stored record in place.
        store.Appended[0] = store.Appended[0] with { EntityId = "TAMPERED" };

        var alerter = new FakeAlerter();
        var verifier = new AuditVerifier(store, alerter);
        var result = await verifier.VerifyPartitionAsync(AuditPartition.KeyFor(store.Appended[0].OccurredAt));

        result.IsIntact.Should().BeFalse();
        alerter.Alerts.Should().ContainSingle();
    }
}
