using System.Text;
using System.Text.Json;
using Mersal.Data;
using Mersal.Notification.Domain;
using Mersal.Notification.Infrastructure;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Notification.Api;

public sealed class DomainEventOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>
    /// The queues this service owns. NOT the domain streams themselves (`approvals.events`, `pharmacy.events`):
    /// the transport is point-to-point, so consumers attached to one queue COMPETE for its messages — binding
    /// to `pharmacy.events` would have RabbitMQ deal each event to either policy-service or this one, and
    /// roughly half of everything would silently go undelivered by whichever did not get it.
    ///
    /// So a publisher that wants a notification enqueues a SECOND, notification-shaped copy here. That is the
    /// same decision `policy.registration-enrolments` and `notification.registration-events` already made.
    /// </summary>
    public string[] Queues { get; set; } = ["notification.domain-events", "notification.registration-events"];
}

/// <summary>
/// THE FAN-OUT SUBSCRIPTION. Turns routed domain events into notifications.
///
/// <para><b>What this replaces.</b> `notification-service` shipped a routing table, bilingual templates for
/// thirteen event types, an escalation model and a dispatcher — and nothing delivered an event to any of it.
/// The README called the subscription "deferred wiring (fanout bus)", and the `/ingest` seam it pointed at had
/// no caller anywhere in the repository. Every auth decision, every out-of-stock line, was published to its
/// domain stream and read by nobody who could notify a human. It is not deferred any more.</para>
///
/// <para><b>Recipients ride on the envelope.</b> `RoutingTable` targets ROLES; resolving a role to people is
/// directory business, and this service is deliberately free of it (see `NotificationEnvelope`). The
/// publishing service already knows who: approvals knows who submitted the authorization, patient knows who
/// filed the registration. So the publisher names them and this maps them onto the route's roles — which also
/// means a notification is addressed to the person who is actually waiting for it rather than broadcast to
/// everyone holding a role, and a request that lands in everybody's inbox lands in nobody's work.</para>
///
/// <para><b>One consumer, any event.</b> Adding a notification for a new event is now a publisher change and a
/// template row — no new consumer, no new queue. The event type comes off the message, and an event with no
/// entry in <see cref="RoutingTable"/> is dropped by the dispatcher with a log rather than failing.</para>
/// </summary>
public sealed class DomainEventConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<DomainEventOptions> options,
    ILogger<DomainEventConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("notification-service-domain-events");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 20, global: false);

            foreach (var queue in opt.Queues)
            {
                _channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
                _channel.BasicConsume(queue, autoAck: false, consumer);
                logger.LogInformation("notification-service consuming domain events from {Queue}", queue);
            }
        }
        catch (Exception ex)
        {
            // Broker unavailable (dev without RabbitMQ): serve the inbox API rather than crash the host.
            // Nothing is lost — events stay durable in each publisher's outbox until they are relayed here.
            logger.LogWarning(ex, "domain-event consumer could not connect; notifications will not be delivered yet");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var eventType = ea.BasicProperties.Type ?? "";
            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var notice = Parse(Encoding.UTF8.GetString(ea.Body.Span));
            if (notice is null || eventType.Length == 0)
            {
                // A message that cannot be attributed to a tenant, or has nobody to tell, is dead-lettered
                // rather than guessed at: an in-app notice written under a guessed tenant is a cross-tenant
                // disclosure, which is worse than a lost doorbell.
                logger.LogWarning("notification envelope {EventId} ({Type}) lacked a tenant or a recipient", eventId, eventType);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant comes off the event envelope.
            sp.GetRequiredService<RlsContext>().TenantId = notice.TenantId;

            var envelope = BuildEnvelope(eventId, eventType, notice);

            var result = await sp.GetRequiredService<NotificationDispatcher>().DispatchAsync(envelope, ct);
            logger.LogInformation(
                "{EventType} notified {Created} recipient(s) (deduplicated: {Dup})",
                eventType, result.Created, result.Deduplicated);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "notification fan-out failed for delivery {Tag}", ea.DeliveryTag);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    internal sealed record Addressee(string UserId, string Role, string Locale);

    internal sealed record Notice(
        string TenantId, string? EntityRef,
        IReadOnlyDictionary<string, string> Fields,
        IReadOnlyList<Addressee> Recipients);

    /// <summary>
    /// Read the notification envelope.
    ///
    /// <para>Two shapes are accepted. The general one carries a `recipients` array of
    /// {userId, role, locale}. The registration notice predates it and carries a single `recipientUserId`
    /// with an implied role — kept because messages published before this deployment are still on the queue,
    /// and dropping them would lose the notices they exist to deliver.</para>
    /// </summary>
    /// <summary>
    /// Shape the parsed notice into the envelope the dispatcher takes: recipients grouped by ROLE, and
    /// de-duplicated by USER within each role.
    ///
    /// <para>Extracted from the receive path so it can be tested. It was inline, wrapped in RabbitMQ delivery
    /// handling, which meant the only way to exercise it was to stand up a broker — so the two decisions it
    /// makes had nothing proving them, and both fail QUIETLY. Losing the grouping sends one message per
    /// recipient instead of one per role, and losing the dedupe notifies the same person twice for one event:
    /// neither errors, and both look like the notification service being noisy.</para>
    /// </summary>
    internal static NotificationEnvelope BuildEnvelope(Guid eventId, string eventType, Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return new NotificationEnvelope(
            eventId, eventType, notice.TenantId, notice.EntityRef, notice.Fields,
            notice.Recipients
                .GroupBy(r => r.Role, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<Recipient>)g
                        .Select(r => new Recipient(r.UserId, r.Locale))
                        .DistinctBy(r => r.UserId, StringComparer.Ordinal)
                        .ToList(),
                    StringComparer.Ordinal));
    }

    internal static Notice? Parse(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var tenantId = Str(root, "tenantId");
        if (string.IsNullOrWhiteSpace(tenantId)) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in f.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) fields[p.Name] = p.Value.GetString()!;
        }
        // The legacy registration notice names its interpolation field directly.
        if (fields.Count == 0 && Str(root, "reference") is { Length: > 0 } reference) fields["ref"] = reference;

        var recipients = new List<Addressee>();
        if (root.TryGetProperty("recipients", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rs.EnumerateArray())
            {
                var userId = Str(r, "userId");
                var role = Str(r, "role");
                if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(role)) continue;
                // Arabic by default: these are Egyptian desk and clinical roles reading an Arabic portal. The
                // dispatcher falls back to the English template when the Arabic one is missing, so a wrong
                // guess degrades to a readable notice rather than to silence.
                recipients.Add(new Addressee(userId, role, Str(r, "locale") ?? Locales.Arabic));
            }
        }
        else if (Str(root, "recipientUserId") is { Length: > 0 } legacy)
        {
            recipients.Add(new Addressee(legacy, "registration_officer", Locales.Arabic));
        }

        // No addressee is not an error worth retrying — it is an event about somebody the publisher could not
        // name, and re-delivering it will not name them.
        return recipients.Count == 0 ? null : new Notice(tenantId, Str(root, "entityRef"), fields, recipients);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
