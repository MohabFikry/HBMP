using System.Text;
using Mersal.Data;
using Mersal.Events;
using Mersal.Reporting.Infrastructure;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Reporting.Api;

public sealed class ProjectionConsumerOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>reporting-service's own queue — see <see cref="ProjectionFeed"/>. Its own, so it competes
    /// with nobody: the transport is point-to-point, and binding <c>policy.events</c> would take half of
    /// eligibility-service's messages.</summary>
    public string Queue { get; set; } = ProjectionFeed.Queue;
}

/// <summary>
/// Feeds the reporting read model. Audit §11.1 item 4.
///
/// <para><b>What was missing.</b> <c>EventProjector</c> and <c>AnalyticsProjector</c> handle twenty event
/// types between them — the authorization queue and its TAT, the enrolment curve, member utilization bands,
/// encounter and appointment counts, drug and modality utilization, cost. Both were complete, both were
/// tested, and the only thing that ever called them was <c>POST /projections</c>, a seam gated on
/// <c>reporting:project</c> with no caller anywhere in the repository. Every fact table was empty;
/// <c>reporting.fact_cost</c> held zero rows. The dashboards rendered, correctly, nothing.</para>
///
/// <para><b>Why this shape.</b> Publishers do not enqueue a reporting-shaped copy the way they do for
/// notification-service, because reporting needs nothing a publisher knows that it cannot read off the event
/// itself — notification needs the RECIPIENT, which only the publisher knows. Instead the outbox relay
/// mirrors the raw message onto this queue (<see cref="ProjectionFeed"/>) and
/// <see cref="ProjectionMapping"/> translates it here. Thirteen services stay ignorant of the read model's
/// field vocabulary, and a schema change here is a change here.</para>
///
/// <para><b>Idempotent by construction.</b> The mirror carries the ORIGINAL <c>MessageId</c>, and
/// <c>EventProjector</c> dedupes on it in the same transaction that writes the facts. A redelivery — from a
/// relay retry, a broker restart, or a nack — is a no-op rather than a double-counted member.</para>
/// </summary>
public sealed class ProjectionConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<ProjectionConsumerOptions> options,
    TimeProvider clock,
    ILogger<ProjectionConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("reporting-service-projections");
            _channel = _connection.CreateModel();
            // A read model is not on anybody's critical path, so it may lag; a deep prefetch keeps a backlog
            // draining at the rate the database will take rather than one message per round trip.
            _channel.BasicQos(0, prefetchCount: 50, global: false);
            _channel.QueueDeclare(opt.Queue, durable: true, exclusive: false, autoDelete: false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.Queue, autoAck: false, consumer);
            logger.LogInformation("reporting-service projecting from {Queue}", opt.Queue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (dev without RabbitMQ): serve the report API rather than crash the host.
            // Nothing is lost — the messages stay durable on the queue until a consumer returns.
            logger.LogWarning(ex, "projection consumer could not connect; the read model will not advance yet");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var publishedType = ea.BasicProperties.Type ?? "";
            // The original event id, which is the whole basis of the dedupe. A message with no parseable id
            // gets a fresh one — it can then be projected twice, so it is better to have it than to drop the
            // fact, and every publisher on this feed sets it.
            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var occurredAt = ea.BasicProperties.Timestamp.UnixTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(ea.BasicProperties.Timestamp.UnixTime)
                : clock.GetUtcNow();

            var ev = ProjectionMapping.TryMap(eventId, publishedType, Encoding.UTF8.GetString(ea.Body.Span), occurredAt);
            if (ev is null)
            {
                // Unattributable to a tenant, or not JSON. Dead-lettered rather than projected under a guessed
                // tenant: every fact table is under RLS, and a row written to the wrong one is one
                // organisation's figures inside another's dashboard.
                logger.LogWarning("projection event {EventId} ({Type}) had no tenant or no readable payload",
                    eventId, publishedType);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant comes off the event envelope.
            sp.GetRequiredService<RlsContext>().TenantId = ev.TenantId;

            var projected = await sp.GetRequiredService<EventProjector>().ProjectAsync(ev, ct);
            if (!projected)
                // Either a redelivery (the common case, and correct) or an event on the feed that no
                // projector claims. Debug rather than warning: the feed is an allow-list, so an unclaimed
                // event means the allow-list and the projectors have drifted — which the drift test catches
                // at build time, not at 3am in a log.
                logger.LogDebug("{Type} ({EventId}) produced no fact", publishedType, eventId);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "projection failed for delivery {Tag}", ea.DeliveryTag);
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
