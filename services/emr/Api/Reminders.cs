using Mersal.Emr.Domain;
using Mersal.Events;

namespace Mersal.Emr.Api;

/// <summary>
/// In-app reminder delivery: enqueues a reminder event for notification-service to fan out to the
/// beneficiary's in-app inbox. Scheduling/identity only — no clinical data.
/// </summary>
/// <remarks>
/// <para><b>The queue was wrong, and silently.</b> This published to <c>notification.events</c>, which nothing
/// consumes — <c>DomainEventConsumer</c> reads <c>notification.domain-events</c> and
/// <c>notification.registration-events</c>. Every appointment reminder ever issued went onto a queue with no
/// reader. A publish to an unbound queue does not fail, so the reminder path looked live from end to end:
/// the scheduler ran, the channel was called, the outbox relayed, and nobody was reminded of anything.</para>
///
/// <para><b>Recipients ride on the envelope</b>, per §11.2 — <c>RoutingTable</c> targets the `beneficiary`
/// role, and resolving a role to a person is directory business notification-service is deliberately free of.
/// emr-service is the one that knows whose appointment this is, so it names them.</para>
///
/// <para>The field bag is min-necessary: the appointment reference the template interpolates as <c>{ref}</c>
/// and nothing else. Not the provider, not the visit kind — a reminder in an inbox is read by whoever has the
/// device, and the clinic a person is attending is not something it needs to say.</para>
/// </remarks>
public sealed class InAppReminderChannel(IOutbox outbox) : IReminderChannel
{
    public ReminderChannel Channel => ReminderChannel.InApp;

    public async Task SendAsync(ReminderMessage m, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(m);
        await outbox.EnqueueAsync("AppointmentReminderIssued", "notification.domain-events", new
        {
            tenantId = m.TenantId,
            entityRef = $"appointment:{m.AppointmentId}",
            fields = new { @ref = m.AppointmentId.ToString() },
            recipients = new[]
            {
                new { userId = m.BeneficiaryId.ToString(), role = "beneficiary", locale = "ar" },
            },
        }, ct);
    }
}

/// <summary>SMS reminder STUB — the interface is wired so a future phase can drop in a real provider; today
/// it only logs. No SMS gateway integration in this phase.</summary>
public sealed class SmsReminderChannelStub(ILogger<SmsReminderChannelStub> log) : IReminderChannel
{
    public ReminderChannel Channel => ReminderChannel.Sms;
    public Task SendAsync(ReminderMessage m, CancellationToken ct = default)
    {
        log.LogInformation("[STUB] SMS reminder ({Kind}) for appointment {AppointmentId} deferred to a future phase",
            m.Kind, m.AppointmentId);
        return Task.CompletedTask;
    }
}

/// <summary>WhatsApp reminder STUB — same contract as the live in-app channel; logging only for now.</summary>
public sealed class WhatsAppReminderChannelStub(ILogger<WhatsAppReminderChannelStub> log) : IReminderChannel
{
    public ReminderChannel Channel => ReminderChannel.WhatsApp;
    public Task SendAsync(ReminderMessage m, CancellationToken ct = default)
    {
        log.LogInformation("[STUB] WhatsApp reminder ({Kind}) for appointment {AppointmentId} deferred to a future phase",
            m.Kind, m.AppointmentId);
        return Task.CompletedTask;
    }
}
