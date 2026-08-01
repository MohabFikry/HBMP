using System.Text;
using System.Text.Json;
using Mersal.Audit.Client;
using Mersal.Audit.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Audit.Infrastructure;

public sealed class RabbitMqOptions
{
    public const string SectionName = "Messaging";
    public string Uri { get; set; } = "amqp://guest:guest@rabbitmq:5672";
    public string Queue { get; set; } = "audit.events";
}

/// <summary>
/// The ONLY write path into the audit store: consumes audit events from RabbitMQ (guaranteed
/// at-least-once) and ingests them (dedupe on event id). No synchronous write path from business
/// services (19-audit-strategy.md §4). Manual ack after a successful append.
/// </summary>
public sealed class RabbitMqAuditConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqAuditConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        var factory = new ConnectionFactory { Uri = new Uri(opt.Uri), DispatchConsumersAsync = true };
        _connection = factory.CreateConnection("audit-service");
        _channel = _connection.CreateModel();
        _channel.QueueDeclare(opt.Queue, durable: true, exclusive: false, autoDelete: false);
        _channel.BasicQos(0, prefetchCount: 20, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
        _channel.BasicConsume(opt.Queue, autoAck: false, consumer);

        logger.LogInformation("audit-service consuming {Queue}", opt.Queue);
        return Task.CompletedTask;
    }

    /*
     * ============================================================================================================
     * THE SAME SERIALIZER THE PUBLISHER USES. EVERY AUDIT EVENT ON THE PLATFORM DEPENDED ON THIS.
     * ============================================================================================================
     * `OutboxBase` writes payloads with `JsonSerializerDefaults.Web` — camelCase, case-insensitive on read.
     * This consumer deserialized with the DEFAULT options, which are PascalCase and case-SENSITIVE, so
     * `auditEventId` never bound to `AuditEventId`. `AuditEvent` declares those properties `required`, so
     * every single message failed with "missing required properties" and was nacked to the dead-letter queue.
     *
     * The failure was invisible in exactly the way that matters: every service emitted its events correctly,
     * every relay published them, the queue drained, and `audit.audit_event` stayed empty — 0 rows across the
     * whole platform. Nothing reported an outage, because from every publisher's point of view the write
     * succeeded. 19-audit-strategy makes the audit trail immutable and hash-chained; none of that helps when
     * the chain has no links.
     *
     * The serializer is shared with the publisher deliberately, rather than set to case-insensitive here: the
     * two sides of one wire format should be one decision, not two that happen to agree.
     */
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var evt = JsonSerializer.Deserialize<AuditEvent>(json, Json)
                      ?? throw new InvalidOperationException("null audit payload");

            using var scope = scopeFactory.CreateScope();
            var ingest = scope.ServiceProvider.GetRequiredService<AuditIngestService>();
            await ingest.IngestAsync(evt, ct);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            // Poison message: nack without requeue → dead-letter (configured on the broker).
            logger.LogError(ex, "audit ingest failed for delivery {Tag}", ea.DeliveryTag);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
