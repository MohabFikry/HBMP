using System.Text;
using Mersal.Data;
using Mersal.Eligibility.Infrastructure;
using Mersal.Events;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Eligibility.Api;

public sealed class ConsumerOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";
    /// <summary>Upstream event queues this service consumes to keep its read models fresh.</summary>
    public string[] Queues { get; set; } = ["patient.events", "policy.events"];
}

/// <summary>
/// Consumes patient + policy domain events (at-least-once) and applies them to the local read models,
/// invalidating cached snapshots. Manual ack after a successful, idempotent apply; poison messages are
/// dead-lettered. Mirrors the audit-service consumer pattern (16-service-architecture).
/// </summary>
public sealed class EventConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<ConsumerOptions> options,
    ConsumerHealthState health,
    ILogger<EventConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("eligibility-service");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 20, global: false);

            foreach (var queue in opt.Queues)
            {
                _channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
                _channel.BasicConsume(queue, autoAck: false, consumer);
                logger.LogInformation("eligibility-service consuming {Queue}", queue);
            }
            health.Connected = true;
        }
        catch (Exception ex)
        {
            // Broker not available (e.g. unit/dev without RabbitMQ): the service still serves checks
            // from projections seeded by other means. Log and continue rather than crash the host.
            health.Connected = false;
            logger.LogWarning(ex, "eligibility event consumer could not connect; running without live event sync");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var eventType = ea.BasicProperties.Type ?? "";
            var payload = Encoding.UTF8.GetString(ea.Body.Span);

            // 18.B2 — a background consumer has no HTTP principal, so it binds the RLS tenant GUC itself.
            // It takes the tenant from the EVENT ENVELOPE; it used to stamp a hardcoded SoleTenantId, which
            // would have routed a second tenant's patient/policy events into the first tenant's eligibility
            // projections — wrong answers, not errors. An unattributable message is dead-lettered instead.
            var tenant = EventTenant.Of(payload);
            if (tenant is null)
            {
                logger.LogError(
                    "eligibility projection refused: {EventType} (delivery {Tag}, event {EventId}) carries no tenantId; " +
                    "dead-lettering rather than guessing a tenant", eventType, ea.DeliveryTag, eventId);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<RlsContext>().TenantId = tenant;
            var updater = scope.ServiceProvider.GetRequiredService<ProjectionUpdater>();
            await updater.ApplyAsync(eventId, eventType, payload, ct);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
            health.MarkEventApplied();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "eligibility projection apply failed for delivery {Tag}", ea.DeliveryTag);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override void Dispose()
    {
        health.Connected = false;
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
