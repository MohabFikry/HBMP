using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Mersal.Events;

public sealed class EventsOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";
    public int RelayBatchSize { get; set; } = 50;
    public int RelayIntervalMs { get; set; } = 1000;
    /// <summary>Poison-message cap: a row that fails this many publish attempts is quarantined (skipped).</summary>
    public int MaxAttempts { get; set; } = 8;
    /// <summary>
    /// Dev/test escape hatch: use the process-local <see cref="InMemoryOutbox"/> instead of the durable
    /// EF outbox. Default false = durable (C1). Set true only in appsettings.Development.json / tests.
    /// </summary>
    public bool UseInMemoryOutbox { get; set; }
}

/// <summary>Publishes a relayed outbox message to a durable RabbitMQ queue (ordered domain events).</summary>
public sealed class RabbitMqEventPublisher(IOptions<EventsOptions> options) : IEventPublisher, IDisposable
{
    private readonly ConnectionFactory _factory = new() { Uri = new Uri(options.Value.RabbitUri) };
    private readonly object _gate = new();
    private IConnection? _connection;
    private IModel? _channel;

    // Connect lazily on first publish (and reconnect if dropped) so an unreachable broker degrades
    // gracefully — the relay marks the message failed and retries — instead of crashing startup.
    private IModel Channel()
    {
        lock (_gate)
        {
            if (_channel is { IsOpen: true }) return _channel;
            _channel?.Dispose();
            if (_connection is not { IsOpen: true })
            {
                _connection?.Dispose();
                _connection = _factory.CreateConnection("hbmp-outbox-relay");
            }
            _channel = _connection!.CreateModel();
            return _channel;
        }
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channel = Channel();
        channel.QueueDeclare(message.Destination, durable: true, exclusive: false, autoDelete: false);

        var props = channel.CreateBasicProperties();
        props.MessageId = message.EventId.ToString();
        props.Type = message.EventType;
        props.CorrelationId = message.CorrelationId;
        props.ContentType = "application/json";
        props.Persistent = true;
        // WHEN THE THING HAPPENED, not when the relay got round to it. `OccurredAt` is stamped inside the
        // business transaction; a consumer that reads its own clock instead records the relay's backlog, and
        // after an outage that reads as an hour of care delivered in one second (ADR-0031's timeline is the
        // first consumer to care). Second precision, which is AMQP's — a step is a moment in a day, not a span.
        props.Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds());

        var body = Encoding.UTF8.GetBytes(message.Payload);
        channel.BasicPublish(exchange: "", routingKey: message.Destination,
            basicProperties: props, body: body);

        /*
         * THE MIRRORS (see ProjectionFeed and CareFeed).
         *
         * A second copy of the SAME body, with the SAME MessageId, onto a consumer's own queue. The transport
         * is point-to-point, so reporting cannot simply subscribe to `policy.events` and emr cannot simply
         * subscribe to `orders.events` — each would compete with the service already bound there and RabbitMQ
         * would deal every event to one of them. And they must not: the publish above is the contract every
         * existing consumer depends on, and this changes nothing about it.
         *
         * Publishing to a mirror is not allowed to lose the original. If a mirror publish throws, the relay
         * marks the whole message failed and retries — which would re-deliver the ORIGINAL too, to consumers
         * that already had it. They are idempotent (every consumer dedupes on event id), so that is
         * survivable, and it is the correct trade: a dashboard silently missing a fact, or a timeline silently
         * missing a step, is worse than a redelivery the consumers are built to absorb.
         *
         * Ordered so the mirrors are a LIST rather than a growing run of near-identical `if` blocks: the third
         * one is where a copy-paste quietly reuses the previous queue name and a whole feed goes to the wrong
         * consumer while every publish still succeeds.
         */
        foreach (var queue in Mirrors(message.EventType))
        {
            channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);
            channel.BasicPublish(exchange: "", routingKey: queue, basicProperties: props, body: body);
        }

        return Task.CompletedTask;
    }

    /// <summary>The mirror queues this event belongs on. An event can be on both — a dispense is a cost fact
    /// for the read model AND a step in the patient's episode, and the two consumers must each get their own
    /// copy rather than one of them winning it.</summary>
    private static IEnumerable<string> Mirrors(string? eventType)
    {
        if (ProjectionFeed.Includes(eventType)) yield return ProjectionFeed.Queue;
        if (CareFeed.Includes(eventType)) yield return CareFeed.Queue;
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
