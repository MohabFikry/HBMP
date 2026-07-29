using System.Text;
using System.Text.Json;
using Mersal.Events;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Identity.Api;

public sealed class ProgramConsumerOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>Upstream queues the issuer tails to keep its projections fresh. Only admin-service's, and only
    /// for programme enablement — the issuer owns every other input to a token.</summary>
    public string[] Queues { get; set; } = ["admin.events"];
}

/// <summary>
/// 21.4 propagation — keeps <c>identity.tenant_feature</c> in step with admin-service, so the `features` claim
/// a token carries is the enablement state a platform administrator actually set (design 40 §4/§5).
///
/// <para>Mirrors the eligibility/audit consumer shape: manual ack after a successful, idempotent apply, and
/// poison messages dead-lettered rather than requeued to spin forever.</para>
///
/// <para><b>Fail-soft on connect, deliberately.</b> If the broker is unreachable the issuer still issues
/// tokens — from the projection as it stands. The alternative, refusing to start, would make a broker outage
/// an authentication outage, which is a far larger failure than a switch that propagates late. What it means
/// in practice: a feature toggled during a broker outage takes effect when the broker returns. That is
/// acceptable for enablement (it is administrative, not a security boundary — the gate can only ever subtract,
/// and every endpoint still enforces its own scope) and it would NOT be acceptable for a permission change,
/// which is precisely why permissions do not travel this way.</para>
/// </summary>
public sealed class ProgramEventConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<ProgramConsumerOptions> options,
    ILogger<ProgramEventConsumer> logger) : BackgroundService
{
    public const string FeatureChangedEvent = "TenantFeatureChanged";

    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("identity-service");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 20, global: false);

            foreach (var queue in opt.Queues)
            {
                _channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
                _channel.BasicConsume(queue, autoAck: false, consumer);
                logger.LogInformation("identity-service consuming {Queue} for programme enablement", queue);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "identity-service programme consumer could not connect; tokens will carry the projection as it stands");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var eventType = ea.BasicProperties.Type ?? "";
        try
        {
            // admin.events carries everything admin-service publishes; this consumer wants one type. Anything
            // else is ACKed, not dead-lettered: it is not a poison message, it is simply not ours, and nacking
            // it would dead-letter another consumer's traffic.
            if (!string.Equals(eventType, FeatureChangedEvent, StringComparison.Ordinal))
            {
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var payload = Encoding.UTF8.GetString(ea.Body.Span);
            var change = FeatureChange.Parse(payload);
            if (change is null)
            {
                logger.LogError(
                    "programme projection refused event {EventId}: payload is not a usable TenantFeatureChanged; " +
                    "dead-lettering rather than guessing which tenant or feature it meant", eventId);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<TenantFeatureStore>();
            var dedupe = scope.ServiceProvider.GetRequiredService<IProcessedEventStore>();

            // ONE transaction over the dedupe claim AND the apply. Split, a crash between them leaves the id
            // marked processed with the projection never updated — and because the id is now known, the
            // redelivery that would have fixed it is discarded. The tenant then holds a stale switch with
            // nothing anywhere reporting a failure.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await dedupe.TryBeginAsync(eventId, ct))
            {
                var applied = await store.ApplyAsync(
                    change.TenantId, change.FeatureKey, change.Enabled, change.ChangedAt, eventId, ct);

                if (!applied)
                {
                    // Not a failure: an out-of-order redelivery whose changed_at is older than what we hold.
                    // Logged because "the switch I set did not take effect" is answered by this line.
                    logger.LogInformation(
                        "programme projection kept newer state for {Tenant}/{Feature}: event {EventId} was stamped {ChangedAt}",
                        change.TenantId, change.FeatureKey, eventId, change.ChangedAt);
                }
            }
            await tx.CommitAsync(ct);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "programme projection apply failed for {EventType} delivery {Tag}", eventType, ea.DeliveryTag);
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

/// <summary>The payload admin-service publishes, parsed defensively — a malformed or partial event must be
/// dead-lettered, never half-applied.</summary>
public sealed record FeatureChange(string TenantId, string FeatureKey, bool Enabled, DateTimeOffset ChangedAt)
{
    public static FeatureChange? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tenantId = Str(root, "tenantId");
            var featureKey = Str(root, "featureKey");
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(featureKey)) return null;

            if (!root.TryGetProperty("enabled", out var en)
                || en.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                // A missing `enabled` is NOT defaulted to false. "Absence means disabled" is a rule about a
                // missing ROW, not about a malformed event, and quietly reading one as "switch it off" would
                // turn a serialization bug into an outage for the tenant.
                return null;
            }

            if (!root.TryGetProperty("changedAt", out var at) || !at.TryGetDateTimeOffset(out var changedAt))
            {
                // Without the stamp the ordering guard has nothing to compare, so the apply could move a row
                // backwards. Refuse rather than substitute "now", which would make a stale redelivery look
                // like the newest truth.
                return null;
            }

            return new FeatureChange(tenantId!, featureKey!, en.GetBoolean(), changedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
