using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

public class ReminderDispatcherTests
{
    /// <summary>Records what it was asked to send — stands in for a real channel (in-app or a stub).</summary>
    private sealed class RecordingChannel(ReminderChannel channel) : IReminderChannel
    {
        public ReminderChannel Channel => channel;
        public List<ReminderMessage> Sent { get; } = [];
        public Task SendAsync(ReminderMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Dispatches_to_the_in_app_channel()
    {
        var inApp = new RecordingChannel(ReminderChannel.InApp);
        var dispatcher = new ReminderDispatcher([inApp]);

        var used = await dispatcher.DispatchAsync("t0", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, ReminderKind.Booked, ReminderChannel.InApp);

        used.Should().Be(ReminderChannel.InApp);
        inApp.Sent.Should().ContainSingle().Which.Kind.Should().Be(ReminderKind.Booked);
    }

    [Fact]
    public async Task Honors_the_preferred_channel_when_registered()
    {
        var inApp = new RecordingChannel(ReminderChannel.InApp);
        var sms = new RecordingChannel(ReminderChannel.Sms);
        var whatsApp = new RecordingChannel(ReminderChannel.WhatsApp);
        var dispatcher = new ReminderDispatcher([inApp, sms, whatsApp]);

        var used = await dispatcher.DispatchAsync("t0", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, ReminderKind.Upcoming, ReminderChannel.WhatsApp);

        used.Should().Be(ReminderChannel.WhatsApp);
        whatsApp.Sent.Should().ContainSingle();
        inApp.Sent.Should().BeEmpty();
        sms.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Falls_back_to_in_app_when_preferred_channel_is_absent()
    {
        // Only the in-app channel is registered — a preference for SMS falls back rather than failing.
        var inApp = new RecordingChannel(ReminderChannel.InApp);
        var dispatcher = new ReminderDispatcher([inApp]);

        var used = await dispatcher.DispatchAsync("t0", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, ReminderKind.Booked, ReminderChannel.Sms);

        used.Should().Be(ReminderChannel.InApp);
        inApp.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Throws_when_no_channel_at_all_is_registered()
    {
        var dispatcher = new ReminderDispatcher([]);
        var act = async () => await dispatcher.DispatchAsync("t0", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, ReminderKind.Booked, ReminderChannel.InApp);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Queue_ordering_is_priority_then_arrival()
    {
        var now = DateTimeOffset.UtcNow;
        var low1 = new QueueTicket { QueueId = Guid.NewGuid(), Priority = 0, EnqueuedAt = now, State = QueueTicketState.Waiting };
        var low2 = new QueueTicket { QueueId = Guid.NewGuid(), Priority = 0, EnqueuedAt = now.AddMinutes(1), State = QueueTicketState.Waiting };
        var high = new QueueTicket { QueueId = Guid.NewGuid(), Priority = 5, EnqueuedAt = now.AddMinutes(2), State = QueueTicketState.Waiting };
        var done = new QueueTicket { QueueId = Guid.NewGuid(), Priority = 9, EnqueuedAt = now, State = QueueTicketState.Done };

        var ordered = QueueRules.Ordered([low2, low1, high, done]).ToList();

        ordered.Should().HaveCount(3);                 // Done is excluded
        ordered[0].Should().Be(high);                  // highest priority first
        ordered[1].Should().Be(low1);                  // then earliest arrival
        ordered[2].Should().Be(low2);
    }
}
