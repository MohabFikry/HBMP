namespace Mersal.Emr.Domain;

/// <summary>Delivery channels for appointment reminders. In-app is live now (via notification-service);
/// SMS/WhatsApp are wired as stubs behind the same interface for a future phase — no provider integration
/// here.</summary>
public enum ReminderChannel { InApp, Sms, WhatsApp }

/// <summary>Why a reminder fires: at booking, or ahead of the scheduled time.</summary>
public enum ReminderKind { Booked, Upcoming }

/// <summary>A reminder to deliver — scheduling/identity only, never clinical data.</summary>
/// <param name="TenantId">
/// The RLS scope of the appointment. Carried because the in-app channel publishes to notification-service,
/// whose consumer has no HTTP principal to read a tenant from and dead-letters a message it cannot attribute
/// — "an in-app notice written under a guessed tenant is a cross-tenant disclosure, which is worse than a
/// lost doorbell."
/// </param>
public sealed record ReminderMessage(
    string TenantId, Guid AppointmentId, Guid BeneficiaryId, Guid ProviderId,
    DateTimeOffset ScheduledStart, ReminderKind Kind, ReminderChannel Channel);

/// <summary>One delivery channel. Implementations register per <see cref="Channel"/>; the dispatcher selects
/// by the beneficiary's preferred channel and falls back to <see cref="ReminderChannel.InApp"/>.</summary>
public interface IReminderChannel
{
    ReminderChannel Channel { get; }
    Task SendAsync(ReminderMessage message, CancellationToken ct = default);
}
