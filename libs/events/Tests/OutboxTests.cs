using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Events;

namespace Mersal.Events.Tests;

public class OutboxTests
{
    [Fact]
    public async Task Enqueue_stages_message_with_type_destination_payload()
    {
        var outbox = new InMemoryOutbox();

        await outbox.EnqueueAsync("OrderLineConsumed", "orders.events", new { orderId = "ORD-1", qty = 2 });

        var msg = outbox.AllMessages.Should().ContainSingle().Subject;
        msg.EventType.Should().Be("OrderLineConsumed");
        msg.Destination.Should().Be("orders.events");
        msg.Payload.Should().Contain("ORD-1");
        msg.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Dequeue_then_mark_processed_drains_the_outbox()
    {
        var outbox = new InMemoryOutbox();
        await outbox.EnqueueAsync("A", "q", new { x = 1 });
        await outbox.EnqueueAsync("B", "q", new { x = 2 });

        var batch = await outbox.DequeueBatchAsync(10);
        batch.Should().HaveCount(2);
        foreach (var m in batch) await outbox.MarkProcessedAsync(m.EventId);

        outbox.AllMessages.Should().OnlyContain(m => m.ProcessedAt != null);
        (await outbox.DequeueBatchAsync(10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_message_is_requeued_for_retry()
    {
        var outbox = new InMemoryOutbox();
        await outbox.EnqueueAsync("A", "q", new { x = 1 });
        var msg = (await outbox.DequeueBatchAsync(1))[0];

        await outbox.MarkFailedAsync(msg.EventId, "broker down");

        var retry = await outbox.DequeueBatchAsync(1);
        retry.Should().ContainSingle();
        retry[0].Attempts.Should().Be(1);
        retry[0].LastError.Should().Be("broker down");
    }
}

public class IdempotentConsumerTests
{
    [Fact]
    public async Task Handler_runs_once_and_duplicate_is_skipped()
    {
        var consumer = new IdempotentConsumer(new InMemoryProcessedEventStore());
        var id = Guid.NewGuid();
        var runs = 0;

        var first = await consumer.HandleAsync(id, _ => { runs++; return Task.CompletedTask; });
        var second = await consumer.HandleAsync(id, _ => { runs++; return Task.CompletedTask; });

        first.Should().BeTrue();
        second.Should().BeFalse(); // duplicate delivery → skipped
        runs.Should().Be(1);
    }
}

public class OutboxAuditSinkTests
{
    [Fact]
    public async Task Audit_client_emits_route_through_the_transactional_outbox()
    {
        var outbox = new InMemoryOutbox();
        IAuditOutbox sink = new OutboxAuditSink(outbox);
        var client = new AuditClient(sink, new AuditClientContext("patient-service"), TimeProvider.System);

        await client.EmitAsync(new AuditEventDraft
        {
            EntityType = "beneficiary", EntityId = "MRS-M-1", Action = AuditAction.Create,
        });

        var msg = outbox.AllMessages.Should().ContainSingle().Subject;
        msg.Destination.Should().Be(OutboxAuditSink.Destination);
        msg.EventType.Should().Be("AuditEventRecorded");
        msg.Payload.Should().Contain("MRS-M-1");
    }
}
