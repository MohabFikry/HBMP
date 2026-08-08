using System.Text;
using System.Text.Json;
using Mersal.Data;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Emr.Api;

public sealed class PractitionerBranchRevokedOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>This consumer's OWN queue, not the shared `provider.events` stream. The transport is
    /// point-to-point, so consumers sharing a queue compete for its messages and each event reaches only one
    /// of them — which is how a second subscriber silently stops seeing anything.</summary>
    public string Queue { get; set; } = "emr.practitioner-branch-revoked";
}

/// <summary>
/// Flag the appointments a practitioner leaves behind when they stop serving a branch.
///
/// <para><b>What this closes.</b> provider-service can end a clinician's branch assignment (14.5). That makes
/// <c>serves-branch</c> false at once, so emr refuses NEW slots and NEW bookings there — but it could do
/// nothing about appointments ALREADY booked with that doctor at that branch, because provider-service does
/// not own appointments and cannot see them. The event was published and nothing consumed it, so the outcome
/// was a patient keeping an appointment with a doctor who no longer worked at that clinic, discovered when
/// they arrived.</para>
///
/// <para><b>Why it only marks.</b> Cancelling would destroy real booked care over an administrative change,
/// with no human deciding — and for a beneficiary who has travelled and lost a day's pay, that is not a cost
/// this consumer gets to impose. Reassigning would silently change who the patient was told they would see,
/// while every board still looked healthy. Both are decisions for the desk, so the appointment is left
/// untouched and simply flagged; the reception board renders the flag and someone rings the patient.</para>
///
/// <para><b>Only FUTURE, still-active appointments.</b> A past appointment already happened — flagging it
/// asks reception to act on something that cannot be changed, and buries the ones that can. Cancelled and
/// completed rows are likewise out.</para>
///
/// <para><b>At-least-once.</b> The <c>processed_event</c> ledger short-circuits a redelivery, so a broker
/// retry cannot re-flag rows a receptionist has already dealt with.</para>
/// </summary>
public sealed class PractitionerBranchRevokedConsumer(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<PractitionerBranchRevokedOptions> options,
    ILogger<PractitionerBranchRevokedConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("emr-service-practitioner-branch");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 10, global: false);
            _channel.QueueDeclare(opt.Queue, durable: true, exclusive: false, autoDelete: false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.Queue, autoAck: false, consumer);
            logger.LogInformation("emr-service consuming practitioner branch revocations from {Queue}", opt.Queue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (dev without RabbitMQ): serve the API rather than crash the host. Nothing is
            // lost — the event stays durable in provider-service's outbox until it is relayed and acked here.
            logger.LogWarning(ex, "practitioner-branch consumer could not connect; revocations will not flag appointments yet");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            // The queue is this consumer's own, so anything else on it is unexpected rather than routine —
            // acked so it cannot block the queue, and the type check stays because a queue nobody else writes
            // to today is not a guarantee about tomorrow.
            if (!string.Equals(ea.BasicProperties.Type ?? "", "PractitionerBranchRevoked", StringComparison.Ordinal))
            {
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var revoked = Parse(Encoding.UTF8.GetString(ea.Body.Span));
            if (revoked is null)
            {
                // Cannot be attributed to a tenant or is missing an id — dead-lettered rather than applied
                // under a guessed tenant, which would flag another organisation's appointments.
                logger.LogWarning("practitioner branch revocation {EventId} lacked a tenant or required field", eventId);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant comes off the event envelope.
            sp.GetRequiredService<RlsContext>().TenantId = revoked.TenantId;

            var db = sp.GetRequiredService<EmrDbContext>();
            if (await db.ProcessedEvents.FindAsync([eventId], ct) is not null)
            {
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var flagged = await FlagAsync(db, revoked, ct);

            db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);

            if (flagged > 0)
            {
                logger.LogInformation(
                    "practitioner {Practitioner} left branch {Branch}: {Count} future appointment(s) flagged for reassignment",
                    revoked.PractitionerId, revoked.BranchId, flagged);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "practitioner branch revocation failed for delivery {Tag}", ea.DeliveryTag);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private async Task<int> FlagAsync(EmrDbContext db, RevokedEvent revoked, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var affected = await db.Appointments
            .Where(a => a.DoctorId == revoked.PractitionerId
                        && a.BranchId == revoked.BranchId
                        && a.ScheduledStart > now
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn)
                        // Already flagged rows keep their ORIGINAL timestamp: it records when the problem
                        // arose, and a redelivery must not make a week-old orphan look like today's news.
                        && a.ReassignmentNeededAt == null)
            .ToListAsync(ct);

        foreach (var a in affected)
        {
            a.ReassignmentNeededAt = now;
            a.UpdatedAt = now;
        }
        return affected.Count;
    }

    private static RevokedEvent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // The envelope shape provider-service publishes; `tenantId` may sit on either level depending on
            // how the outbox wrapped it, so both are accepted rather than assuming one.
            var payload = root.TryGetProperty("data", out var d) ? d : root;

            if (!payload.TryGetProperty("practitionerId", out var p) || !p.TryGetGuid(out var practitionerId)) return null;
            if (!payload.TryGetProperty("branchId", out var b) || !b.TryGetGuid(out var branchId)) return null;

            var tenant = Str(payload, "tenantId") ?? Str(root, "tenantId");
            if (string.IsNullOrWhiteSpace(tenant)) return null;

            return new RevokedEvent(tenant, practitionerId, branchId);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed record RevokedEvent(string TenantId, Guid PractitionerId, Guid BranchId);
}
