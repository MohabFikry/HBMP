using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Mersal.Events.Tests;

/// <summary>
/// The prior-authorization saga's two mirrors, proven against a REAL broker.
/// </summary>
/// <remarks>
/// <para><b>Why against a real broker and not a fake.</b> Everything about a mirror that can go wrong is a
/// property of the transport, not of the code that decides which queue to name. The publish goes to the
/// DEFAULT exchange with the destination as the routing key, so a queue that is never declared silently
/// discards its messages; a queue declared with different arguments raises PRECONDITION_FAILED at declare
/// time; and a second consumer on an existing queue competes rather than duplicates. A test with a stub
/// <c>IModel</c> asserts that <c>Mirrors()</c> returned a string — which was never the part in doubt. This
/// asserts a message published as <c>OrderPendingApproval</c> can actually be READ OUT of
/// <c>approvals.routing-events</c>, which is the claim the saga rests on.</para>
/// <para><b>Env-gated on <c>HBMP_TEST_RABBIT_URI</c></b>, the same shape as the DB-gated suites. It skips
/// where no broker is reachable rather than failing, and — because a skipped proof is worth nothing — the
/// gate is a variable CI and <c>./dotnet.sh test --with-db</c> can both set.</para>
/// <para><b>It cleans up after itself</b> by consuming what it published from every queue it touched. These
/// are the real, durable, shared queues; leaving a synthetic order on <c>approvals.routing-events</c> would
/// hand a dev-stack consumer a message about an order that does not exist.</para>
/// </remarks>
public class SagaMirrorAgainstBrokerTests
{
    private static readonly string? Uri = Environment.GetEnvironmentVariable("HBMP_TEST_RABBIT_URI");

    [SkippableTheory]
    [InlineData("OrderPendingApproval")]
    [InlineData("RxSubmitted")]
    public void A_routed_event_reaches_the_approvals_routing_queue(string eventType)
    {
        Skip.If(Uri is null, "HBMP_TEST_RABBIT_URI not set — broker integration test skipped.");

        var marker = Guid.NewGuid();
        Publish(eventType, marker, "orders.events");

        Drain("orders.events", marker).Should().BeTrue("the original publish is untouched by the mirror");
        Drain(ApprovalRoutingFeed.Queue, marker).Should().BeTrue(
            "without this copy a gated order changes status, tells the patient to wait, and reaches no reviewer");
    }

    [SkippableFact]
    public void A_decision_reaches_BOTH_owners_rather_than_whichever_won_the_race()
    {
        Skip.If(Uri is null, "HBMP_TEST_RABBIT_URI not set — broker integration test skipped.");

        var marker = Guid.NewGuid();
        Publish("AuthApproved", marker, "approvals.events");

        // The point of two queues. One shared queue would have the broker deal each decision to orders OR
        // pharmacy — and the loser's order would stay PendingApproval with no error anywhere.
        Drain(ApprovalDecisionFeed.OrdersQueue, marker).Should().BeTrue();
        Drain(ApprovalDecisionFeed.PharmacyQueue, marker).Should().BeTrue();

        // And the original stream still carries it, for anything bound there later.
        Drain("approvals.events", marker).Should().BeTrue();
    }

    [SkippableFact]
    public void An_unrelated_event_is_not_mirrored_anywhere_near_the_saga()
    {
        Skip.If(Uri is null, "HBMP_TEST_RABBIT_URI not set — broker integration test skipped.");

        // The allow-list is the whole design: `AuthInfoRequested` settles nothing, so releasing an order on
        // it would move a request that is still open.
        var marker = Guid.NewGuid();
        Publish("AuthInfoRequested", marker, "approvals.events");

        Drain(ApprovalDecisionFeed.OrdersQueue, marker).Should().BeFalse();
        Drain(ApprovalDecisionFeed.PharmacyQueue, marker).Should().BeFalse();
        Drain("approvals.events", marker).Should().BeTrue("the original stream is unconditional");
    }

    private static void Publish(string eventType, Guid marker, string destination)
    {
        using var publisher = new RabbitMqEventPublisher(
            Options.Create(new EventsOptions { RabbitUri = Uri! }));
        publisher.PublishAsync(new OutboxMessage
        {
            EventId = marker,
            EventType = eventType,
            Destination = destination,
            Payload = $"{{\"marker\":\"{marker}\"}}",
            OccurredAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
    }

    /// <summary>Take messages off <paramref name="queue"/> until the marker is found or the queue is empty.
    /// Everything it takes it ACKS, including messages it did not publish — see the note below.</summary>
    private static bool Drain(string queue, Guid marker)
    {
        using var connection = new ConnectionFactory { Uri = new Uri(Uri!) }.CreateConnection("hbmp-saga-mirror-test");
        using var channel = connection.CreateModel();
        channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);

        var body = $"\"{marker}\"";
        var foreign = new List<ulong>();
        // Bounded: a queue with a real backlog must not turn this into an unbounded read, and finding the
        // marker behind a hundred real messages would mean the dev stack is mid-outage — a different problem,
        // and not one this test should hide by grinding through it.
        var found = false;
        for (var i = 0; i < 100 && !found; i++)
        {
            var got = channel.BasicGet(queue, autoAck: false);
            if (got is null) break;

            if (Encoding.UTF8.GetString(got.Body.Span).Contains(body, StringComparison.Ordinal))
            {
                channel.BasicAck(got.DeliveryTag, multiple: false);
                found = true;
            }
            // NOT ours: held un-acked so the read can continue past it, and handed back below. Acking
            // somebody else's message would delete a real event off a shared dev queue, which is precisely
            // the failure this whole change exists to remove.
            else foreign.Add(got.DeliveryTag);
        }

        foreach (var tag in foreign) channel.BasicNack(tag, multiple: false, requeue: true);
        return found;
    }
}
