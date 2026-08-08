using System.Text;
using Mersal.Data;
using Mersal.Emr.Infrastructure;
using Mersal.Events;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Emr.Api;

public sealed class CareEpisodeConsumerOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>emr's OWN queue, fed by the <see cref="CareFeed"/> mirror. Deliberately not
    /// <c>orders.events</c> or <c>pharmacy.events</c>: the transport is point-to-point, so binding there would
    /// make this consumer compete with policy-service's benefit accumulator and RabbitMQ would deal each
    /// event to one of them — half the timeline missing AND half the coverage accumulator frozen, with
    /// nothing in either service's log to say so.</summary>
    public string Queue { get; set; } = CareFeed.Queue;
}

/// <summary>
/// The other half of the care episode (ADR-0031): the steps emr does not perform itself.
///
/// <para><b>What this closes.</b> emr already recorded the visit it owns — started, vitals, diagnosis, note,
/// ended. Everything that visit CAUSED happened in other services: the investigation, the approval it waited
/// on, the sample, the result, the prescription, the dispense. Each was recorded faithfully in its own
/// service and joined up in none, so an appointment's timeline stopped at the consulting-room door. A desk
/// asking "why is this member still here at four o'clock?" could see the visit start and could not see that
/// it was waiting on an authorization raised at eleven.</para>
///
/// <para><b>The two things it refuses to do.</b> It will not write a step under a guessed tenant — a message
/// with no <c>tenantId</c> is dead-lettered, because a step stamped with the wrong tenant is not a missing
/// row, it is another organisation's patient history. And it will not carry clinical content: the translation
/// in <see cref="CareEpisodeMapping"/> reads names, numbers and actors, and the payload's test codes, drugs,
/// result values and rationales are left where they are.</para>
///
/// <para><b>At-least-once.</b> Dedupe is the <c>ux_care_timeline_event</c> unique index, not a ledger in this
/// process — a consumer that restarts has forgotten what it processed and the database has not. See
/// <see cref="CareEpisodeAppender"/>.</para>
/// </summary>
public sealed class CareEpisodeConsumer(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<CareEpisodeConsumerOptions> options,
    ILogger<CareEpisodeConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("emr-service-care-episode");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 20, global: false);
            _channel.QueueDeclare(opt.Queue, durable: true, exclusive: false, autoDelete: false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.Queue, autoAck: false, consumer);
            logger.LogInformation("emr-service consuming care-episode steps from {Queue}", opt.Queue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (dev without RabbitMQ): serve the API rather than crash the host. Nothing is
            // lost — the events stay durable in each producer's outbox until relayed and acked here. The
            // timeline is then INCOMPLETE, which is survivable, rather than WRONG, which is not.
            logger.LogWarning(ex, "care-episode consumer could not connect; orders/pharmacy/approvals steps will not appear on timelines yet");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var eventType = ea.BasicProperties.Type ?? "";
            var payload = Encoding.UTF8.GetString(ea.Body.Span);

            var draft = CareEpisodeMapping.For(eventType, payload);
            if (draft is null)
            {
                // Not a step, or carries no encounter to attach one to. Acked: the mirror's allow-list and the
                // mapping switch are edited at different times by different hands, and a message that falls in
                // the gap was never owed an answer.
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var tenantId = EventTenant.Of(payload);
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                logger.LogWarning("care-episode event {Type} carried no tenant; dead-lettered rather than stamped with a guess", eventType);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            // WHEN IT HAPPENED, from the publisher's outbox row (stamped inside the business transaction),
            // falling back to now. Reading this clock instead would time every step at the moment the relay
            // caught up — so a backlog would render as an hour of care delivered in one second, and the
            // ordering a timeline exists to show would be the relay's, not the patient's.
            var occurredAt = ea.BasicProperties.Timestamp.UnixTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(ea.BasicProperties.Timestamp.UnixTime)
                : clock.GetUtcNow();

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant comes off the event envelope.
            sp.GetRequiredService<RlsContext>().TenantId = tenantId;

            var outcome = await sp.GetRequiredService<CareEpisodeAppender>().AppendAsync(draft, eventId, occurredAt, ct);
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);

            if (outcome == CareStepOutcome.UnknownEncounter)
                logger.LogInformation(
                    "care-episode step {Step} named encounter {Encounter}, which this tenant has no record of; not stepped",
                    draft.Step, draft.EncounterId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "care-episode step failed for delivery {Tag}", ea.DeliveryTag);
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
