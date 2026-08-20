using System.Text;
using System.Text.Json;
using Mersal.Data;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Mersal.Time;

namespace Mersal.Policy.Api;

public sealed class RegistrationEnrolmentOptions
{
    public const string SectionName = "Events";
    public string RabbitUri { get; set; } = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>A queue of this consumer's OWN, not the shared `patient.events` stream. The transport is
    /// point-to-point, so consumers on one queue compete for its messages — sharing it with
    /// eligibility-service would have had RabbitMQ deal each event to one of them and the other would never
    /// see it.</summary>
    public string Queue { get; set; } = "policy.registration-enrolments";
}

/// <summary>
/// Turn an APPROVED registration into a membership.
///
/// <para><b>What this closes.</b> The coverage an officer elects at the desk is stored on the registration as
/// an intent, and <c>registration.coverage_bound</c> has always claimed a policy was bound. Until now nothing
/// acted on it: a supervisor approved a registration, a member number was issued, and the person had no
/// membership — so every eligibility check for them answered "not covered" while every screen showed them
/// Active. The gap was invisible precisely because both halves looked right on their own.</para>
///
/// <para><b>Why an event rather than a call from patient-service.</b> Approval must not fail because
/// policy-service is restarting, and coverage must not be silently skipped because a call timed out. The
/// event is written in the same transaction as the activation, so the enrolment is guaranteed to happen
/// eventually and cannot happen without the approval.</para>
///
/// <para><b>At-least-once, handled twice.</b> The <c>processed_event</c> ledger short-circuits a redelivered
/// event id, and the enrolment itself carries a business idempotency key, so even a redelivery under a fresh
/// event id returns the existing membership rather than creating a second one.</para>
/// </summary>
public sealed class RegistrationEnrolmentConsumer(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<RegistrationEnrolmentOptions> options,
    ILogger<RegistrationEnrolmentConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(opt.RabbitUri), DispatchConsumersAsync = true };
            _connection = factory.CreateConnection("policy-service-enrolment");
            _channel = _connection.CreateModel();
            _channel.BasicQos(0, prefetchCount: 10, global: false);
            _channel.QueueDeclare(opt.Queue, durable: true, exclusive: false, autoDelete: false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
            _channel.BasicConsume(opt.Queue, autoAck: false, consumer);
            logger.LogInformation("policy-service consuming registration enrolments from {Queue}", opt.Queue);
        }
        catch (Exception ex)
        {
            // Broker unavailable (dev without RabbitMQ): serve the API rather than crash the host. Nothing is
            // lost — the event is durable in patient-service's outbox until it is relayed and acked here.
            logger.LogWarning(ex, "registration-enrolment consumer could not connect; approvals will not enrol yet");
        }
        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var eventType = ea.BasicProperties.Type ?? "";
            // The queue is this consumer's own, so anything else on it is unexpected rather than routine —
            // acked so it cannot block the queue, and the type check stays because a queue nobody else writes
            // to today is not a guarantee about tomorrow.
            if (!string.Equals(eventType, "RegistrationEnrolmentRequested", StringComparison.Ordinal))
            {
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid();
            var request = Parse(Encoding.UTF8.GetString(ea.Body.Span));
            if (request is null)
            {
                // A message we cannot attribute to a tenant is dead-lettered rather than written under a
                // guessed one — the same rule the consumption consumer follows.
                logger.LogWarning("registration enrolment {EventId} lacked a tenant or required field", eventId);
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            // Background consumer — no HTTP principal — so the RLS tenant comes off the event envelope.
            sp.GetRequiredService<RlsContext>().TenantId = request.TenantId;

            var db = sp.GetRequiredService<PolicyDbContext>();
            if (await db.ProcessedEvents.FindAsync(new object[] { eventId }, ct) is not null)
            {
                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            await EnrolAsync(sp, db, request, ct);

            db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, ProcessedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "registration enrolment failed for delivery {Tag}", ea.DeliveryTag);
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private async Task EnrolAsync(IServiceProvider sp, PolicyDbContext db, EnrolmentRequest request, CancellationToken ct)
    {
        // The intent names a PLAN (the product the officer chose). A membership is against a POLICY PLAN —
        // that plan as sold under a specific policy — so resolve the live one. If the plan is offered under
        // more than one policy the choice is not ours to make: an arbitrary pick is a coin toss over which
        // payer funds this person's care, so it is refused and left for a human.
        var today = BusinessCalendar.DateIn(clock.GetUtcNow());

        // policy_plan → plan_version → plan, restricted to live rows under an Active policy.
        var matches = await (
            from pp in db.PolicyPlans.AsNoTracking().Where(x => !x.IsDeleted)
            join v in db.PlanVersions.AsNoTracking().Where(x => x.PlanId == request.PlanId)
                on pp.PlanVersionId equals v.PlanVersionId
            join p in db.Policies.AsNoTracking().Where(x => !x.IsDeleted && x.Status == PolicyStatus.Active)
                on pp.PolicyId equals p.PolicyId
            select pp).ToListAsync(ct);

        if (matches.Count != 1)
        {
            logger.LogWarning(
                "registration enrolment for {MemberNo}: plan {PlanId} resolves to {Count} live policy plans; not enrolled",
                request.MemberNo, request.PlanId, matches.Count);
            return;
        }

        var membership = sp.GetRequiredService<MembershipCommands>();
        var command = new EnrollCommand(
            request.BeneficiaryId, matches[0].PolicyId, matches[0].PolicyPlanId, GroupId: null,
            "Principal", PrincipalEnrollmentId: null, request.EffectiveFrom ?? today, EffectiveTo: null,
            request.BranchId, AgeYears: null);

        // Keyed on the REGISTRATION, so a redelivery under a new event id still returns the membership the
        // first delivery created rather than colliding with the overlap exclusion.
        var key = BulkIdempotency.KeyFor($"registration:{request.RegistrationId}");
        var actor = new ActorRef(null, "registration-approval");
        // The event is emitted only by a successful Approve, in the same transaction that set the beneficiary
        // Active and issued their member number — so the status is not assumed, it was just written. Saying so
        // is what lets this run without a token of its own.
        var result = await membership.EnrollAsync(
            command, key, bearerToken: null, actor, establishedStatus: "Active", ct);
        if (!result.Ok)
        {
            logger.LogWarning("registration enrolment for {MemberNo} refused: {Code} {Detail}",
                request.MemberNo, result.Error!.Code, result.Error.Detail);
            return;
        }

        // The member-level cost share the officer elected. Written after the membership exists, because it
        // is an override ON it — the plan's own matrix remains the default for everything not stated here.
        var enrollment = await db.Enrollments.FirstOrDefaultAsync(
            e => e.EnrollmentId == result.Value!.Enrollment.EnrollmentId, ct);
        if (enrollment is not null)
        {
            enrollment.NetworkTierId = request.NetworkTierId;
            enrollment.ContributionPercent = request.ContributionPercent;
            enrollment.UpdatedAt = clock.GetUtcNow();
        }

        logger.LogInformation("registration {RegistrationId} enrolled {MemberNo} as {EnrollmentId}",
            request.RegistrationId, request.MemberNo, result.Value!.Enrollment.EnrollmentId);
    }

    internal sealed record EnrolmentRequest(
        string TenantId, Guid RegistrationId, Guid BeneficiaryId, string? MemberNo,
        Guid PlanId, Guid? NetworkTierId, decimal? ContributionPercent, Guid? BranchId, DateOnly? EffectiveFrom);

    /// <summary>Read the envelope, refusing anything that cannot be attributed to a tenant and a person.</summary>
    internal static EnrolmentRequest? Parse(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var tenantId = Str(root, "tenantId");
        var registrationId = GuidOf(root, "registrationId");
        var beneficiaryId = GuidOf(root, "beneficiaryId");
        var planId = GuidOf(root, "planId");
        if (string.IsNullOrWhiteSpace(tenantId) || registrationId is null || beneficiaryId is null || planId is null)
            return null;

        return new EnrolmentRequest(
            tenantId, registrationId.Value, beneficiaryId.Value, Str(root, "memberNo"), planId.Value,
            GuidOf(root, "networkTierId"), DecimalOf(root, "contributionPercent"), GuidOf(root, "branchId"),
            DateOf(root, "effectiveFrom"));
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Guid? GuidOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && Guid.TryParse(v.GetString(), out var g) ? g : null;

    private static decimal? DecimalOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static DateOnly? DateOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateOnly.TryParse(v.GetString(), out var d) ? d : null;

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
