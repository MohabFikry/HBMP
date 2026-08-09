using System.Text;
using System.Text.Json;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Data;
using Mersal.Events;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Approvals.Api;

public sealed class RoutingConsumerOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>approvals-service's own routing queue — see <see cref="ApprovalRoutingFeed"/> for why it is
    /// its own and not a second consumer on <c>orders.events</c>.</summary>
    public string RoutingQueue { get; set; } = ApprovalRoutingFeed.Queue;

    /// <summary>How many times one message may be requeued for a transient failure before it is parked on the
    /// dead-letter queue instead. Same bound, and the same reasoning, as <see cref="FulfilmentConsumerOptions.MaxRedeliveries"/>.</summary>
    public int MaxRedeliveries { get; set; } = 5;
}

/// <summary>
/// Raises an authorization for every order and prescription that routing sent for a decision — the FORWARD
/// leg of the prior-authorization saga.
/// </summary>
/// <remarks>
/// <para><b>Why asynchronous.</b> The same argument the fulfilment consumer makes, pointing the other way.
/// An authorization that cannot be raised must never be able to fail the ORDER: the clinician has recorded
/// what the patient needs, and refusing to accept an order because approvals-service is restarting would
/// push the failure onto the person least able to do anything about it. The producers enqueue through their
/// durable outbox inside the order/prescription transaction, so nothing is lost while this is down — the
/// order simply waits a little longer, which is what it was going to do anyway.</para>
/// <para><b>At-least-once, guarded twice.</b> The <c>processed_event</c> ledger short-circuits a redelivered
/// message id; the PRIMARY KEY on <c>processed_request</c> stops two concurrent deliveries of the SAME
/// message — this runs at prefetch 20, so the ledger read and the insert are not one act. See the note in
/// <see cref="RoutedAuthorizationIngestor"/> for why the guard is deliberately NOT the order id.</para>
/// </remarks>
public sealed class RoutingConsumer(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<RoutingConsumerOptions> options,
    ILogger<RoutingConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IConnection? _connection;
    private IModel? _channel;

    /// <summary>Requeue attempts per message. Kept here rather than read off the broker for the reason the
    /// fulfilment consumer records: <c>Redelivered</c> is a flag, not a tally.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _attempts = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("approvals-service-routing");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 20, global: false);

            _channel.QueueDeclare(opt.RoutingQueue, durable: true, exclusive: false, autoDelete: false);
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.RoutingQueue, autoAck: false, consumer);
            logger.LogInformation("approvals-service consuming routed requests from {Queue}", opt.RoutingQueue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (unit/dev without RabbitMQ): serve the API rather than crash the host. No
            // authorization is raised until the broker returns; nothing is lost, because the events are
            // durable in each producer's outbox until relayed and acked here.
            logger.LogWarning(ex, "approvals routing consumer could not connect; routed requests are not reaching the worklist");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        // A key that is the SAME message on every redelivery — see FulfilmentConsumer for why the ledger's
        // event id will not do.
        var retryKey = Guid.TryParse(ea.BasicProperties.MessageId, out var messageId)
            ? messageId
            : new Guid(System.Security.Cryptography.SHA256.HashData(ea.Body.Span).AsSpan(0, 16));
        try
        {
            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var eventType = ea.BasicProperties.Type ?? "";
            var msg = JsonSerializer.Deserialize<RoutingMessage>(Encoding.UTF8.GetString(ea.Body.Span), Json);

            var invalid = msg is null ? "unparseable" : RoutedAuthorizationIngestor.Validate(eventType, msg);
            if (invalid is not null)
            {
                // Dead-letter, do not requeue: a message we cannot trust will not become trustworthy by being
                // delivered again, and an authorization pointing at no real order looks like a request.
                logger.LogError("routed request refused ({Reason}); dead-lettered", invalid);
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
                var result = await sp.GetRequiredService<RoutedAuthorizationIngestor>()
                    .IngestAsync(eventId, eventType, msg, ct);

                db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(ct);

                // Audited only when something was actually created. An ungated prescription is the majority
                // of this queue and produces no record — an audit row per non-event would bury the ones that
                // matter, which is the same argument the reviewer inbox makes for defaulting to kind=Review.
                if (result.Outcome == RoutingOutcome.Raised)
                {
                    await sp.GetRequiredService<IAuditClient>().EmitAsync(new AuditEventDraft
                    {
                        EntityType = "authorization",
                        EntityId = result.AuthorizationId!.Value.ToString(),
                        Action = AuditAction.Create,
                        TenantId = msg.TenantId,
                        ActorUserId = msg.OrderedByUserId,
                        DecisionOutcome = "Submitted",
                        AfterState = $"{{\"authNo\":\"{result.AuthNo}\",\"kind\":\"Review\",\"source\":\"{eventType}\"}}",
                    }, ct);
                }

                if (result.Outcome == RoutingOutcome.Refused)
                    logger.LogError("routed request not raised: {Reason}", result.Reason);
            }

            _attempts.TryRemove(retryKey, out _);
            _channel?.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            // Requeue: an infrastructure failure, not a bad message. Bounded, for the reason the fulfilment
            // consumer records — a message that breaks deterministically after deserialization comes straight
            // back at prefetch 20 and pins the queue.
            var attempt = _attempts.AddOrUpdate(retryKey, 1, (_, n) => n + 1);
            if (attempt > options.Value.MaxRedeliveries)
            {
                _attempts.TryRemove(retryKey, out _);
                logger.LogCritical(ex,
                    "routed request {RetryKey} failed {Attempts} times; dead-lettered rather than requeued " +
                    "again. AN ORDER OR PRESCRIPTION IS WAITING ON A DECISION THAT HAS NO REQUEST — replay it " +
                    "from hbmp.dead-letter once the cause is fixed.", retryKey, attempt);
                _channel?.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            logger.LogError(ex, "routed request could not be processed (attempt {Attempt}); requeued", attempt);
            try { await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct); }
            catch (OperationCanceledException) { /* shutting down: fall through and hand the message back */ }
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
