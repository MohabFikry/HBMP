using System.Text;
using System.Text.Json;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Data;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Approvals.Api;

public sealed class FulfilmentConsumerOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>
    /// approvals-service's OWN queue, and it has to be its own.
    /// </summary>
    /// <remarks>
    /// The transport is point-to-point. policy-service already consumes <c>pharmacy.events</c> and
    /// <c>orders.events</c> to move the benefit accumulator, so a second consumer bound to either would
    /// COMPETE for messages — each dispense would reach one service and not the other, and the accumulator
    /// would silently stop moving for every event approvals happened to win. A service that wants its own
    /// copy is sent its own copy; <c>notification.domain-events</c> established this.
    /// </remarks>
    public string FulfilmentQueue { get; set; } = "approvals.fulfilments";
}

/// <summary>
/// Issues a fulfilment authorization for every dispense and every consume (ADR-0034).
/// </summary>
/// <remarks>
/// <para><b>Why asynchronous.</b> An authorization that cannot be issued must never be able to fail a
/// dispense. The patient has the medicine; a bookkeeping record catching up thirty seconds later is correct,
/// and refusing to hand medicine over because approvals-service is restarting is not. The producers enqueue
/// through their durable outbox inside the dispense transaction, so nothing is lost while this is down.</para>
/// <para><b>At-least-once, guarded twice.</b> The <c>processed_event</c> ledger short-circuits a redelivered
/// message id; the UNIQUE <c>(tenant_id, fulfilment_ref)</c> on the item stops a redelivery that arrives
/// under a NEW message id, which the ledger cannot see. Only the second guard survives a replay the first
/// has forgotten.</para>
/// </remarks>
public sealed class FulfilmentConsumer(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<FulfilmentConsumerOptions> options,
    ILogger<FulfilmentConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("approvals-service");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 20, global: false);

            _channel.QueueDeclare(opt.FulfilmentQueue, durable: true, exclusive: false, autoDelete: false);
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.FulfilmentQueue, autoAck: false, consumer);
            logger.LogInformation("approvals-service consuming fulfilments from {Queue}", opt.FulfilmentQueue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (unit/dev without RabbitMQ): serve the API rather than crash the host. The
            // register simply does not advance until the broker returns; nothing is lost, because the events
            // are durable in each producer's outbox until relayed and acked here.
            logger.LogWarning(ex, "approvals fulfilment consumer could not connect; no authorizations are being issued");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var msg = JsonSerializer.Deserialize<FulfilmentMessage>(Encoding.UTF8.GetString(ea.Body.Span), Json);

            var invalid = msg is null ? "unparseable" : FulfilmentIssuer.Validate(msg);
            if (invalid is not null)
            {
                // Dead-letter, do not requeue: a message we cannot trust will not become trustworthy by being
                // delivered again, and an authorization stamped with a guessed tenant looks like a record.
                logger.LogError("fulfilment message refused ({Reason}); dead-lettered", invalid);
                _channel?.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant GUC is bound from the envelope.
            // Validate() already refused any message without one.
            sp.GetRequiredService<RlsContext>().TenantId = msg!.TenantId!;

            var db = sp.GetRequiredService<ApprovalsDbContext>();
            if (await db.ProcessedEvents.FindAsync([eventId], ct) is null)
            {
                var result = await sp.GetRequiredService<FulfilmentIssuer>().IssueAsync(msg, ct);

                db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(ct);

                await sp.GetRequiredService<IAuditClient>().EmitAsync(new AuditEventDraft
                {
                    EntityType = "authorization",
                    EntityId = result.AuthorizationId?.ToString() ?? msg.SourceRef!,
                    Action = AuditAction.Create,
                    TenantId = msg.TenantId,
                    ActorUserId = msg.ActorUserId,
                    DecisionOutcome = result.Outcome.ToString(),
                    AfterState = $"{{\"authNo\":\"{result.AuthNo}\",\"kind\":\"Fulfilment\",\"source\":\"{msg.Source}\",\"sourceRef\":\"{msg.SourceRef}\"}}",
                }, ct);

                if (result.Outcome == FulfilmentOutcome.Rejected)
                    logger.LogError("fulfilment for {SourceRef} not issued: {Reason}", msg.SourceRef, result.Reason);
            }

            _channel?.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            // Requeue: this is an infrastructure failure (DB down, transient), not a bad message. The
            // producer's outbox has already committed, so losing it here would lose the only record that
            // the medicine was handed over.
            logger.LogError(ex, "fulfilment message could not be processed; requeued");
            _channel?.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
