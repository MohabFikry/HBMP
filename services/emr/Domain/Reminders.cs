namespace Mersal.Emr.Domain;

/// <summary>Delivery channels for appointment reminders. In-app is live now (via notification-service);
/// SMS/WhatsApp are wired as stubs behind the same interface for a future phase — no provider integration
/// here.</summary>
public enum ReminderChannel { InApp, Sms, WhatsApp }

/// <summary>Why a reminder fires: at booking, or ahead of the scheduled time.</summary>
public enum ReminderKind { Booked, Upcoming }

/// <summary>A reminder to deliver — scheduling/identity only, never clinical data.</summary>
public sealed record ReminderMessage(
    Guid AppointmentId, Guid BeneficiaryId, Guid ProviderId,
    DateTimeOffset ScheduledStart, ReminderKind Kind, ReminderChannel Channel);

/// <summary>One delivery channel. Implementations register per <see cref="Channel"/>; the dispatcher selects
/// by the beneficiary's preferred channel and falls back to <see cref="ReminderChannel.InApp"/>.</summary>
public interface IReminderChannel
{
    ReminderChannel Channel { get; }
    Task SendAsync(ReminderMessage message, CancellationToken ct = default);
}
