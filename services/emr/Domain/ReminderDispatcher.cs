namespace Mersal.Emr.Domain;

/// <summary>Selects a delivery channel by the beneficiary's preferred channel and dispatches the reminder,
/// falling back to <see cref="ReminderChannel.InApp"/> when the preferred channel is not registered (e.g.
/// the SMS/WhatsApp stubs are absent in a given deployment). Pure over <see cref="IReminderChannel"/> so the
/// selection + fallback + pluggability are unit-testable without transport.</summary>
public sealed class ReminderDispatcher(IEnumerable<IReminderChannel> channels)
{
    public async Task<ReminderChannel> DispatchAsync(
        Guid appointmentId, Guid beneficiaryId, Guid providerId, DateTimeOffset scheduledStart,
        ReminderKind kind, ReminderChannel preferred, CancellationToken ct = default)
    {
        var channel = channels.FirstOrDefault(c => c.Channel == preferred)
                      ?? channels.FirstOrDefault(c => c.Channel == ReminderChannel.InApp)
                      ?? throw new InvalidOperationException("No reminder channel registered (in-app is required as the fallback).");

        var message = new ReminderMessage(appointmentId, beneficiaryId, providerId, scheduledStart, kind, channel.Channel);
        await channel.SendAsync(message, ct);
        return channel.Channel;
    }
}
