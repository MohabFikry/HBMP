using System.Diagnostics;
using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Audit.Client.Tests;

public class AuditClientTests
{
    private static AuditClient ClientWith(InMemoryAuditOutbox outbox) =>
        new(outbox, new AuditClientContext("patient-service"), TimeProvider.System);

    [Fact]
    public async Task Emit_enqueues_event_with_service_identity_and_timestamp()
    {
        var outbox = new InMemoryAuditOutbox();
        var client = ClientWith(outbox);

        await client.EmitAsync(new AuditEventDraft
        {
            EntityType = "beneficiary", EntityId = "MRS-M-1", Action = AuditAction.Create,
            ActorUserId = "u-1", FieldClasses = ["pii"],
        });

        outbox.Events.Should().ContainSingle();
        var e = outbox.Events[0];
        e.ServiceName.Should().Be("patient-service");
        e.SourceService.Should().Be("patient-service");
        e.EntityId.Should().Be("MRS-M-1");
        e.AuditEventId.Should().NotBe(Guid.Empty);
        e.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Emit_stamps_correlation_id_from_ambient_trace()
    {
        using var activity = new Activity("test").Start();
        var outbox = new InMemoryAuditOutbox();

        await ClientWith(outbox).EmitAsync(new AuditEventDraft
        {
            EntityType = "beneficiary", EntityId = "MRS-M-2", Action = AuditAction.Read,
        });

        outbox.Events[0].CorrelationId.Should().Be(activity.TraceId.ToString());
    }

    [Fact]
    public void Wiring_replaces_null_auth_sink_with_audit_bridge_and_events_flow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // libs/auth would register NullAuthEventSink; simulate that:
        services.AddSingleton<IAuthEventSink>(NullAuthEventSink.Instance);
        services.AddHbmpAuditClient("patient-service", useInMemoryOutbox: true);

        var provider = services.BuildServiceProvider();
        var sink = provider.GetRequiredService<IAuthEventSink>();
        sink.Should().BeOfType<AuditAuthEventSink>();

        // A login event should land in the audit outbox as a Login action.
        sink.Record(new AuthEvent(AuthEventKind.LoginSuccess, "user-9", SessionId: "sess-1"));

        var outbox = provider.GetRequiredService<InMemoryAuditOutbox>();
        outbox.Events.Should().ContainSingle()
            .Which.Should().Match<AuditEvent>(e =>
                e.Action == AuditAction.Login && e.EntityType == "identity" && e.ActorUserId == "user-9");
    }
}
