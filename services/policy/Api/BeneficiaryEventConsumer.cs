using System.Text;
using System.Text.Json;
using Mersal.Data;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mersal.Policy.Api;

public sealed class BeneficiaryEventOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>This consumer's OWN queue. The transport is point-to-point, so consumers sharing a queue
    /// compete for its messages — see <see cref="RegistrationEnrolmentOptions"/> for the same reasoning.</summary>
    public string Queue { get; set; } = "policy.beneficiary-events";
}

/// <summary>
/// Put a correction to the identity record on the member's history.
///
/// <para><b>Why policy-service consumes it.</b> The member's Logs tab reads `policy.entity_timeline`, scoped to
/// a membership. patient-service owns the identity record and cannot write into another service's projection,
/// so it publishes what happened and this projects it — which is also what keeps the timeline from becoming a
/// second log that drifts from the audit trail: nothing here decides anything, it only records.</para>
///
/// <para><b>One beneficiary, possibly several memberships.</b> A person can hold more than one enrollment, and
/// a correction to their name is true of all of them. An entry is projected per live membership rather than
/// once against the beneficiary, because the Logs tab is opened FROM a membership and an entry filed anywhere
/// else is an entry nobody reads.</para>
///
/// <para><b>Field names, not values.</b> The event carries WHICH fields changed and no old or new values. The
/// history is read by roles whose projection of the identity record is narrower than the officer's who made
/// the edit, and "the date of birth was corrected" is the part everyone may see. The values are in the audit
/// trail, behind `audit:read`.</para>
/// </summary>
public sealed class BeneficiaryEventConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<BeneficiaryEventOptions> options,
    TimeProvider clock,
    ILogger<BeneficiaryEventConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("policy-service-beneficiary-events");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 10, global: false);
            _channel.QueueDeclare(opt.Queue, durable: true, exclusive: false, autoDelete: false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.Queue, autoAck: false, consumer);
            logger.LogInformation("policy-service consuming beneficiary events from {Queue}", opt.Queue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (dev without RabbitMQ): serve the API rather than crash the host. Nothing is
            // lost — the event is durable in patient-service's outbox until it is relayed and acked here.
            logger.LogWarning(ex, "beneficiary-event consumer could not connect; corrections will not reach the timeline yet");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var eventType = ea.BasicProperties.Type ?? "";
            if (!string.Equals(eventType, "BeneficiaryDetailsCorrected", StringComparison.Ordinal))
            {
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var correction = Parse(Encoding.UTF8.GetString(ea.Body.Span), clock.GetUtcNow());
            if (correction is null)
            {
                logger.LogWarning("beneficiary correction {EventId} lacked a tenant or a beneficiary", eventId);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant comes off the event envelope.
            sp.GetRequiredService<RlsContext>().TenantId = correction.TenantId;

            var db = sp.GetRequiredService<PolicyDbContext>();
            var enrollments = await db.Enrollments.AsNoTracking()
                .Where(e => e.BeneficiaryId == correction.BeneficiaryId)
                .Select(e => e.EnrollmentId)
                .ToListAsync(ct);

            if (enrollments.Count == 0)
            {
                // A person with no membership yet — registered but not approved. There is no history to file
                // against, and that is not an error: the correction is in patient-service's own record and in
                // the audit trail either way.
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            // The projector dedupes on the SOURCE EVENT ID, so one id across several memberships would collapse
            // to a single entry on whichever was projected first. Each membership gets its own derived id —
            // deterministic, so a redelivery still produces the same rows rather than duplicates.
            var sources = enrollments.Select(enrollmentId => new TimelineSource(
                EventId: DerivedId(eventId, enrollmentId),
                EventType: "BeneficiaryDetailsCorrected",
                Scope: NoteScope.Member,
                ScopeRef: enrollmentId,
                OccurredAt: correction.OccurredAt,
                SourceService: "patient-service",
                ActorUserId: Guid.TryParse(correction.ActorUserId, out var actor) ? actor : null,
                ActorDisplay: correction.ActorName,
                // Which fields moved, with no values — see the class note.
                Changes: correction.ChangedFields.ToDictionary(
                    f => f, _ => ((string?)null, (string?)"changed"), StringComparer.Ordinal)));

            var written = await sp.GetRequiredService<TimelineProjector>()
                .ProjectAsync(sources, correction.TenantId, ct);
            logger.LogInformation(
                "beneficiary {BeneficiaryId} correction projected onto {Written} membership timeline(s)",
                correction.BeneficiaryId, written);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "beneficiary correction failed for delivery {Tag}", ea.DeliveryTag);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    /// <summary>A stable per-membership event id: same inputs, same id, so redelivery is a no-op.</summary>
    internal static Guid DerivedId(Guid eventId, Guid enrollmentId)
    {
        Span<byte> buffer = stackalloc byte[32];
        eventId.TryWriteBytes(buffer[..16]);
        enrollmentId.TryWriteBytes(buffer[16..]);
        return new Guid(System.Security.Cryptography.SHA256.HashData(buffer)[..16]);
    }

    internal sealed record Correction(
        string TenantId, Guid BeneficiaryId, IReadOnlyList<string> ChangedFields,
        string? ActorUserId, string? ActorName, DateTimeOffset OccurredAt);

    /// <summary>Read the envelope, refusing anything that cannot be attributed to a tenant and a person.</summary>
    /// <param name="receivedAt">
    /// 18.A3 — the fallback for an event that carries no <c>occurredAt</c>, passed IN rather than read from
    /// the wall clock here.
    ///
    /// This read the wall clock directly, which the bare-clock architecture gate refuses for two reasons that
    /// both bite on this line. It is untestable: no boundary test can pin the timestamp a redelivered
    /// event lands on, so the one case worth asserting — a malformed publisher whose events all fall back —
    /// cannot be asserted at all. And it is a value that reaches `entity_timeline`, which the member's Logs
    /// tab renders as a Cairo DATE: every evening between 22:00 Cairo and midnight UTC, a correction filed
    /// today would be filed under yesterday.
    ///
    /// The caller passes the injected clock's instant, so the fallback is as testable as the parsed path.
    /// </param>
    internal static Correction? Parse(string payload, DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var tenantId = Str(root, "tenantId");
        var beneficiaryId = Str(root, "beneficiaryId");
        if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(beneficiaryId, out var beneficiary))
            return null;

        var fields = root.TryGetProperty("changedFields", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                 .Select(x => x.GetString()!).ToList()
            : [];
        // An edit that moved nothing is not history. patient-service already short-circuits it, so this is a
        // guard against a future publisher that does not.
        if (fields.Count == 0) return null;

        var occurred = root.TryGetProperty("occurredAt", out var at)
            && at.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(at.GetString(), out var parsed)
                ? parsed : receivedAt;

        return new Correction(tenantId, beneficiary, fields, Str(root, "actorUserId"), Str(root, "actorName"), occurred);
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
