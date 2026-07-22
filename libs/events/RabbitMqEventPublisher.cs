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
}

/// <summary>Publishes a relayed outbox message to a durable RabbitMQ queue (ordered domain events).</summary>
public sealed class RabbitMqEventPublisher : IEventPublisher, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;

    public RabbitMqEventPublisher(IOptions<EventsOptions> options)
    {
        var factory = new ConnectionFactory { Uri = new Uri(options.Value.RabbitUri) };
        _connection = factory.CreateConnection("hbmp-outbox-relay");
        _channel = _connection.CreateModel();
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _channel.QueueDeclare(message.Destination, durable: true, exclusive: false, autoDelete: false);

        var props = _channel.CreateBasicProperties();
        props.MessageId = message.EventId.ToString();
        props.Type = message.EventType;
        props.CorrelationId = message.CorrelationId;
        props.ContentType = "application/json";
        props.Persistent = true;

        _channel.BasicPublish(exchange: "", routingKey: message.Destination,
            basicProperties: props, body: Encoding.UTF8.GetBytes(message.Payload));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
    }
}
