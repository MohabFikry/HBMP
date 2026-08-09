using System.Text;
using System.Text.Json;
using Mersal.Audit.Client;
using Mersal.Data;
using Mersal.Events;
using Mersal.Orders.Infrastructure;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Orders.Api;

public sealed class ApprovalDecisionConsumerOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>orders-service's OWN decision queue — see <see cref="ApprovalDecisionFeed"/> for why orders
    /// and pharmacy each get their own copy rather than sharing <c>approvals.events</c>.</summary>
    public string DecisionQueue { get; set; } = ApprovalDecisionFeed.OrdersQueue;

    /// <summary>How many times one message may be requeued for a transient failure before it is parked on the
    /// dead-letter queue instead.</summary>
    public int MaxRedeliveries { get; set; } = 5;
}

/// <summary>
/// Releases (or terminally refuses) the order an authorization decision was about.
/// </summary>
/// <remarks>
/// <para><b>Why this service is a consumer at all.</b> orders-service published <c>OrderPendingApproval</c>
/// and nothing came back: no service consumed <c>approvals.events</c>, so the transitions
/// <c>PendingApproval → Approved → Active</c> that <c>OrderWorkflow</c> declares were never executed by
/// anything. This is the code that executes them.</para>
/// <para><b>Why asynchronous rather than a callback from approvals.</b> The reviewer must be able to reject a
/// request while orders-service is restarting; a synchronous release would make one service's availability a
/// precondition for the other's decisions. The one place the platform DOES call synchronously — a validity
/// extension — is coupled on purpose, because there the reviewer must get both halves or neither.</para>
/// <para><b>At-least-once, guarded twice.</b> The <c>processed_event</c> ledger short-circuits a redelivered
/// message id; the workflow guard stops a redelivery arriving under a NEW id, because
/// <c>Approved → Approved</c> is not a legal transition and the applier reports <c>NotWaiting</c> rather
/// than moving anything or emitting a second event.</para>
/// </remarks>
public sealed class ApprovalDecisionConsumer(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<ApprovalDecisionConsumerOptions> options,
    ILogger<ApprovalDecisionConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IConnection? _connection;
    private IModel? _channel;

    /// <summary>Requeue attempts per message: <c>Redelivered</c> is a flag, not a tally, so the budget is
    /// kept here. It resets on restart, which gives a message another chance rather than inheriting a
    /// verdict from a process that may itself have been the problem.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _attempts = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("orders-service-approvals");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 20, global: false);

            _channel.QueueDeclare(opt.DecisionQueue, durable: true, exclusive: false, autoDelete: false);
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.DecisionQueue, autoAck: false, consumer);
            logger.LogInformation("orders-service consuming authorization decisions from {Queue}", opt.DecisionQueue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (unit/dev without RabbitMQ): serve the API rather than crash the host. Gated
            // orders stay PendingApproval until the broker returns; nothing is lost, because the decision is
            // durable in approvals' outbox until relayed and acked here.
            logger.LogWarning(ex, "orders approval-decision consumer could not connect; approved orders are not being released");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var retryKey = Guid.TryParse(ea.BasicProperties.MessageId, out var messageId)
            ? messageId
            : new Guid(System.Security.Cryptography.SHA256.HashData(ea.Body.Span).AsSpan(0, 16));
        try
        {
            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var msg = JsonSerializer.Deserialize<ApprovalDecisionMessage>(Encoding.UTF8.GetString(ea.Body.Span), Json);

            if (msg is null || string.IsNullOrWhiteSpace(msg.TenantId) || string.IsNullOrWhiteSpace(msg.SourceRef))
            {
                // Dead-letter, do not requeue: a decision we cannot attribute to a tenant or an order will
                // not become attributable by being delivered again, and applying it under a guessed tenant
                // would release somebody else's order.
                logger.LogError("authorization decision refused (no tenant or no sourceRef); dead-lettered");
                _channel?.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant GUC is bound from the envelope.
            sp.GetRequiredService<RlsContext>().TenantId = msg.TenantId;

            var db = sp.GetRequiredService<OrdersDbContext>();
            if (await db.ProcessedEvents.FindAsync([eventId], ct) is null)
            {
                var result = await sp.GetRequiredService<OrderApprovalApplier>().ApplyAsync(msg, ct);

                db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(ct);

                // Audited when the order actually moved. A decision about a prescription arrives here too
                // (both queues get every decision) and produces nothing — an audit row per ignored message
                // would bury the state changes among them.
                if (result.Outcome is ApprovalApplyOutcome.Released or ApprovalApplyOutcome.Rejected)
                {
                    await sp.GetRequiredService<IAuditClient>().EmitAsync(new AuditEventDraft
                    {
                        EntityType = "investigation_order",
                        EntityId = result.OrderId!.Value.ToString(),
                        Action = AuditAction.StateChange,
                        TenantId = msg.TenantId,
                        BeforeState = result.Detail,
                        AfterState = result.Outcome == ApprovalApplyOutcome.Released ? "Active" : "Rejected",
                        DecisionOutcome = result.Outcome.ToString(),
                        DecisionReasonCode = $"authorization:{msg.AuthNo}",
                        BreakGlass = msg.BreakGlass,
                    }, ct);
                }

                if (result.Outcome == ApprovalApplyOutcome.NotWaiting)
                    logger.LogWarning(
                        "authorization {AuthNo} decided, but order {OrderNo} was not waiting on it ({Detail})",
                        msg.AuthNo, result.OrderNo, result.Detail);
            }

            _attempts.TryRemove(retryKey, out _);
            _channel?.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            var attempt = _attempts.AddOrUpdate(retryKey, 1, (_, n) => n + 1);
            if (attempt > options.Value.MaxRedeliveries)
            {
                _attempts.TryRemove(retryKey, out _);
                logger.LogCritical(ex,
                    "authorization decision {RetryKey} failed {Attempts} times; dead-lettered rather than " +
                    "requeued again. AN ORDER IS STILL WAITING ON A DECISION THAT HAS ALREADY BEEN MADE — " +
                    "replay it from hbmp.dead-letter once the cause is fixed.", retryKey, attempt);
                _channel?.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            logger.LogError(ex, "authorization decision could not be applied (attempt {Attempt}); requeued", attempt);
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
