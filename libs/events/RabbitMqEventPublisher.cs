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

        channel.BasicPublish(exchange: "", routingKey: message.Destination,
            basicProperties: props, body: Encoding.UTF8.GetBytes(message.Payload));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
