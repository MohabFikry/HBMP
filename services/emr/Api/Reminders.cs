using Mersal.Emr.Domain;
using Mersal.Events;

namespace Mersal.Emr.Api;

/// <summary>In-app reminder delivery (live now): enqueues a reminder event for notification-service (phase 8)
/// to fan out to the beneficiary's in-app inbox. Scheduling/identity only — no clinical data.</summary>
public sealed class InAppReminderChannel(IOutbox outbox) : IReminderChannel
{
    public ReminderChannel Channel => ReminderChannel.InApp;

    public async Task SendAsync(ReminderMessage m, CancellationToken ct = default) =>
        await outbox.EnqueueAsync("AppointmentReminderIssued", "notification.events", new
        {
            appointmentId = m.AppointmentId, beneficiaryId = m.BeneficiaryId, providerId = m.ProviderId,
            scheduledStart = m.ScheduledStart, kind = m.Kind.ToString(), channel = m.Channel.ToString(),
        }, ct);
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
