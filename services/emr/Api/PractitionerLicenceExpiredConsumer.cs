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

public sealed class PractitionerLicenceExpiredOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>This consumer's OWN queue, not the shared `provider.events` stream. The transport is
    /// point-to-point, so consumers sharing a queue compete for its messages and each event reaches only one
    /// of them — which is how a second subscriber silently stops seeing anything. `PractitionerBranchRevoked`
    /// learned this in 24.3 after publishing to a queue nothing was bound to.</summary>
    public string Queue { get; set; } = "emr.practitioner-licence-expired";
}

/// <summary>
/// 25.3 (design 42 §3) — flag the appointments a lapsed licence leaves behind. FLAG. Never cancel.
///
/// <para><b>What this closes.</b> From the moment a licence expires, emr's two gates refuse NEW slots and NEW
/// bookings for that practitioner on any date past the expiry. Neither can do anything about the
/// appointments ALREADY booked — provider-service does not own appointments and cannot see them — so without
/// this the failure surfaces as a patient keeping an appointment with a doctor who may not lawfully see
/// them, discovered on the day.</para>
///
/// <para><b>Why it only marks, and this one is not a close call.</b> An automated cancellation lands on a
/// refugee who may have no reliable phone number, who has arranged childcare and lost a day's pay to
/// travel, and who has no way to tell a cancellation from being dropped. The clinic's administrative problem
/// must not become that person's. A human decides who covers the clinic; the system's job is to make sure
/// nobody has to notice on their own. Design 42 §7 rule 6, and the same reasoning
/// <c>PractitionerBranchRevokedConsumer</c> already applies.</para>
///
/// <para><b>NEVER RETROACTIVE.</b> Only appointments scheduled AFTER the expiry date are flagged. Care
/// already given was given under a valid licence and stays untouched — flagging it would ask reception to act
/// on something that cannot be changed, and would bury the appointments that can. An appointment ON the
/// expiry date is also left alone: the licence is valid through that day (inclusive boundary,
/// <c>PractitionerLicence.IsValidAt</c>).</para>
///
/// <para><b>At-least-once.</b> The <c>processed_event</c> ledger short-circuits a redelivery, so a broker
/// retry cannot re-flag rows a receptionist has already dealt with.</para>
/// </summary>
public sealed class PractitionerLicenceExpiredConsumer(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<PractitionerLicenceExpiredOptions> options,
    ILogger<PractitionerLicenceExpiredConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("emr-service-practitioner-licence");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 10, global: false);
            _channel.QueueDeclare(opt.Queue, durable: true, exclusive: false, autoDelete: false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.Queue, autoAck: false, consumer);
            logger.LogInformation("emr-service consuming practitioner licence expiries from {Queue}", opt.Queue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (dev without RabbitMQ): serve the API rather than crash the host. Nothing is
            // lost — the event stays durable in provider-service's outbox until it is relayed and acked here.
            logger.LogWarning(ex, "practitioner-licence consumer could not connect; expiries will not flag appointments yet");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            if (!string.Equals(ea.BasicProperties.Type ?? "", "PractitionerLicenceExpired", StringComparison.Ordinal))
            {
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var expired = Parse(Encoding.UTF8.GetString(ea.Body.Span));
            if (expired is null)
            {
                // Cannot be attributed to a tenant or is missing a required field — dead-lettered rather than
                // applied under a guessed tenant, which would flag another organisation's appointments.
                logger.LogWarning("practitioner licence expiry {EventId} lacked a tenant or required field", eventId);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant comes off the event envelope.
            sp.GetRequiredService<RlsContext>().TenantId = expired.TenantId;

            var db = sp.GetRequiredService<EmrDbContext>();
            if (await db.ProcessedEvents.FindAsync([eventId], ct) is not null)
            {
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var flagged = await FlagAsync(db, expired, ct);

            db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);

            if (flagged > 0)
            {
                logger.LogInformation(
                    "practitioner {Practitioner} licence expired {Expiry}: {Count} future appointment(s) flagged for reassignment (none cancelled)",
                    expired.PractitionerId, expired.LicenceExpiry, flagged);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "practitioner licence expiry failed for delivery {Tag}", ea.DeliveryTag);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    /// <summary>
    /// Flag every future appointment this practitioner holds that falls AFTER the expiry date. Exposed
    /// internal so the tests drive the same code the broker does — a consumer whose logic is only reachable
    /// through RabbitMQ is a consumer that gets tested by hand, once.
    /// </summary>
    internal async Task<int> FlagAsync(EmrDbContext db, LicenceExpiredEvent expired, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // The first instant that is NOT covered by the licence: 00:00 Cairo on the day after expiry. Computed
        // as an instant because appointments are instants, and computed in CAIRO because the certificate is
        // about clinic days — doing this in UTC would leave the first two or three hours of the day after
        // expiry unflagged, which is precisely the morning clinic.
        //
        // Normalized to UTC before it reaches the query. The instant is identical either way, but Npgsql
        // refuses to write a non-zero offset to `timestamptz` — the same trap SlotGeneration documents, which
        // there made every call to POST /appointment-slots fail with an unhandled 500.
        var firstUncoveredDay = expired.LicenceExpiry.AddDays(1);
        var cutoff = new DateTimeOffset(
            firstUncoveredDay.ToDateTime(TimeOnly.MinValue), CairoOffsetOn(firstUncoveredDay))
            .ToUniversalTime();

        var affected = await db.Appointments
            .Where(a => a.DoctorId == expired.PractitionerId
                        && a.ScheduledStart >= cutoff
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

    private static TimeSpan CairoOffsetOn(DateOnly on)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Egypt Standard Time" : "Africa/Cairo");
            return tz.GetUtcOffset(on.ToDateTime(TimeOnly.MinValue));
        }
        catch (TimeZoneNotFoundException) { return TimeSpan.FromHours(2); }
        catch (InvalidTimeZoneException) { return TimeSpan.FromHours(2); }
    }

    internal static LicenceExpiredEvent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // `tenantId` may sit on either level depending on how the outbox wrapped it, so both are accepted
            // rather than assuming one — the same tolerance PractitionerBranchRevoked needed.
            var payload = root.TryGetProperty("data", out var d) ? d : root;

            if (!payload.TryGetProperty("practitionerId", out var p) || !p.TryGetGuid(out var practitionerId)) return null;
            if (!payload.TryGetProperty("licenceExpiry", out var e)) return null;
            if (!DateOnly.TryParse(e.GetString(), out var expiry)) return null;

            var tenant = Str(payload, "tenantId") ?? Str(root, "tenantId");
            if (string.IsNullOrWhiteSpace(tenant)) return null;

            return new LicenceExpiredEvent(tenant, practitionerId, expiry);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal sealed record LicenceExpiredEvent(string TenantId, Guid PractitionerId, DateOnly LicenceExpiry);

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
