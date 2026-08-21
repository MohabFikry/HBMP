using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Data;
using Mersal.Notification.Api;
using Mersal.Notification.Domain;
using Mersal.Notification.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mersal.Notification.Tests;

/// <summary>
/// The two background services, past the seams the earlier pass could reach.
///
/// <para><b>Why this file exists.</b> `services/notification:Api` sat at 66.8% with the two background
/// classes at 45.4% and 18.4%. `EnvelopeAndSweepTests` covered the parse seam and the tenant probe — the two
/// pieces that were already static and pure. What it could not reach was everything wrapped in transport: the
/// receive path's judgement, and the sweep's per-tenant loop. Both of those FAIL SILENTLY, which is why they
/// are worth reaching rather than writing off:</para>
///
/// <list type="bullet">
/// <item>A bad message requeued instead of dead-lettered spins at the head of the queue for ever, starving
/// every notification behind it. Nothing errors; the service looks busy.</item>
/// <item>A sweep that binds one tenant and moves on escalates nothing for the rest and returns success,
/// because it did everything it was asked to. `EscalationSweeper`'s own header names this.</item>
/// </list>
///
/// <para>Reached by extracting `HandleAsync` from the RabbitMQ handler, exactly as `BuildEnvelope` was
/// extracted before it, and by running the sweeper over a REAL DI scope rather than a stand-in — the thing
/// under test is which tenant the scope is bound to, and a fake scope would be testing the fake.</para>
/// </summary>
[Collection("notification-db")]
public class ConsumerAndSweeperTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("NOTIFICATION_TEST_DB");

    // ---------------------------------------------------------------- the receive path

    /// <summary>A scope factory over the real service graph, bound to the test database.</summary>
    private static ServiceProvider Services(TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddScoped<RlsContext>();
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddDbContext<NotificationDbContext>(o => o.UseNpgsql(Db).UseSnakeCaseNamingConvention());
        services.AddSingleton(new NotificationOptions());
        services.AddSingleton<IEmailProvider, NullEmail>();
        services.AddScoped<INotificationChannel, InAppChannel>();
        services.AddSingleton<IAuditOutbox, InMemoryAuditOutbox>();
        services.AddScoped<IAuditClient>(sp => new AuditClient(
            sp.GetRequiredService<IAuditOutbox>(),
            new AuditClientContext("notification-test"),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<EscalationService>();
        return services.BuildServiceProvider();
    }

    private sealed class NullEmail : IEmailProvider
    {
        public Task SendAsync(string r, string s, string b, string l, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static DomainEventConsumer Consumer(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DomainEventOptions()),
            NullLogger<DomainEventConsumer>.Instance);

    /// <summary>
    /// A well-formed envelope for `AuthApproved`.
    ///
    /// <para>The role is `requesting_provider` because that is what `RoutingTable` routes AuthApproved to. A
    /// role the route does not name produces an Ack and NO notification — correct behaviour, and the first
    /// draft of this fixture used `medical_approval` and asserted a row that could never appear. Worth the
    /// sentence: the same mistake in a publisher is a notification nobody receives and nothing reports.</para>
    /// </summary>
    private static string Payload(string tenant, string user) => $$"""
        {"tenantId":"{{tenant}}","entityRef":"authorization:abc",
         "fields":{"ref":"AUTH-2026-0001"},
         "recipients":[{"userId":"{{user}}","role":"requesting_provider","locale":"en"}]}
        """;

    [SkippableFact]
    public async Task A_message_with_no_tenant_is_DEAD_LETTERED_never_requeued()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        await using var sp = Services();

        var outcome = await Consumer(sp).HandleAsync(
            "AuthApproved", Guid.NewGuid().ToString(),
            """{"entityRef":"authorization:abc","recipients":[{"userId":"u-1","role":"medical_approval"}]}""",
            deliveryTag: 1, CancellationToken.None);

        // Requeue is the tempting choice and it is the wrong one. The tenant will not appear on a second
        // delivery of the same bytes, so the message returns to the head of the queue and spins there,
        // starving every notification behind it — the one failure mode that looks like the service working.
        outcome.Should().Be(DomainEventConsumer.Outcome.DeadLetter);
    }

    [SkippableFact]
    public async Task A_message_with_no_event_TYPE_is_dead_lettered_too()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        await using var sp = Services();

        // The type comes off the AMQP property, not the body, so a publisher that sets one and not the other
        // produces a well-formed envelope nobody can route. The routing table is keyed on it.
        var outcome = await Consumer(sp).HandleAsync(
            "", Guid.NewGuid().ToString(), Payload("t-x", "u-1"), deliveryTag: 2, CancellationToken.None);

        outcome.Should().Be(DomainEventConsumer.Outcome.DeadLetter);
    }

    [SkippableFact]
    public async Task A_good_message_is_acked_and_the_notification_lands_under_the_ENVELOPES_tenant()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        var tenant = "t-cons-" + Guid.NewGuid().ToString("N")[..10];
        await using var sp = Services();
        try
        {
            var outcome = await Consumer(sp).HandleAsync(
                "AuthApproved", Guid.NewGuid().ToString(), Payload(tenant, "u-1"),
                deliveryTag: 3, CancellationToken.None);

            outcome.Should().Be(DomainEventConsumer.Outcome.Ack);

            // THE assertion, and the reason a real DI scope is used: there is no HTTP principal on this path,
            // so the RLS tenant comes off the envelope. Bound wrongly, the row is written under someone
            // else's tenant — a cross-tenant disclosure that no error reports and no test with a fake scope
            // could see.
            await using var db = Ctx();
            var rows = await db.Notifications.AsNoTracking()
                .IgnoreQueryFilters().Where(n => n.TenantId == tenant).ToListAsync();
            rows.Should().NotBeEmpty("the dispatcher wrote under the tenant the envelope named");
        }
        finally { await CleanupAsync(tenant); }
    }

    [SkippableFact]
    public async Task A_handler_that_throws_dead_letters_rather_than_taking_the_consumer_down()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        // A provider with no dispatcher registered: GetRequiredService throws inside the handler, standing in
        // for any downstream failure. The delivery must be disposed of, not escape — an exception out of an
        // AsyncEventingBasicConsumer handler leaves the message unacked and the channel in an unclear state.
        var bare = new ServiceCollection();
        bare.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        bare.AddScoped<RlsContext>();
        await using var sp = bare.BuildServiceProvider();

        var outcome = await Consumer(sp).HandleAsync(
            "AuthApproved", Guid.NewGuid().ToString(), Payload("t-y", "u-1"),
            deliveryTag: 4, CancellationToken.None);

        outcome.Should().Be(DomainEventConsumer.Outcome.DeadLetter);
    }

    [SkippableFact]
    public async Task An_unreachable_broker_does_not_bring_the_service_down()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        await using var sp = Services();
        var consumer = new DomainEventConsumer(
            sp.GetRequiredService<IServiceScopeFactory>(),
            // A port nothing is listening on. Dev without RabbitMQ is the ordinary case, and the inbox API
            // must still serve: nothing is lost, because events stay durable in each publisher's outbox
            // until they are relayed here.
            Options.Create(new DomainEventOptions { RabbitUri = "amqp://guest:guest@127.0.0.1:1/" }),
            NullLogger<DomainEventConsumer>.Instance);

        var start = () => consumer.StartAsync(CancellationToken.None);
        await start.Should().NotThrowAsync("a missing broker degrades delivery, it does not fail the host");
        await consumer.StopAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- the sweep

    [SkippableFact]
    public async Task The_sweep_visits_EVERY_tenant_with_something_due_not_just_the_first()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        var a = "t-swa-" + Guid.NewGuid().ToString("N")[..10];
        var b = "t-swb-" + Guid.NewGuid().ToString("N")[..10];
        await using var sp = Services();
        try
        {
            await SeedDueAsync(a);
            await SeedDueAsync(b);

            var swept = await new EscalationSweeper(
                sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EscalationSweeper>.Instance).SweepAsync(CancellationToken.None);

            swept.Should().BeGreaterThanOrEqualTo(2);

            // THE property this test exists for. There is no principal on a maintenance pass, and an unbound
            // session reads nothing after 18.F3 — so the sweep binds each tenant in turn. Bind one and stop,
            // or bind a blank GUC, and it escalates nothing for everybody else while returning success,
            // because it did everything it was asked to. Two tenants is the smallest case that can tell the
            // difference; one would pass against a loop that runs exactly once.
            await using var db = Ctx();
            foreach (var t in new[] { a, b })
            {
                var escalated = await db.Notifications.AsNoTracking().IgnoreQueryFilters()
                    .CountAsync(n => n.TenantId == t && n.EscalatedAt != null);
                escalated.Should().Be(1, "tenant {0} had a due escalation and the sweep must have visited it", t);
            }
        }
        finally { await CleanupAsync(a); await CleanupAsync(b); }
    }

    [SkippableFact]
    public async Task A_second_pass_escalates_nothing_again()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        var tenant = "t-swi-" + Guid.NewGuid().ToString("N")[..10];
        await using var sp = Services();
        try
        {
            await SeedDueAsync(tenant);
            var sweeper = new EscalationSweeper(
                sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EscalationSweeper>.Instance);

            (await sweeper.SweepAsync(CancellationToken.None)).Should().BeGreaterThan(0);

            // Idempotence is what lets this run on every node with no leader election: what is due is a
            // property of the rows, not of where the last pass got to. Without it, a two-node deployment
            // escalates everything twice and the supervisor's inbox doubles.
            await using var db = Ctx();
            var after = await db.Notifications.AsNoTracking().IgnoreQueryFilters()
                .CountAsync(n => n.TenantId == tenant && n.EscalatedAt != null);

            await sweeper.SweepAsync(CancellationToken.None);

            await using var db2 = Ctx();
            (await db2.Notifications.AsNoTracking().IgnoreQueryFilters()
                .CountAsync(n => n.TenantId == tenant && n.EscalatedAt != null))
                .Should().Be(after);
        }
        finally { await CleanupAsync(tenant); }
    }

    [SkippableFact]
    public async Task A_failed_pass_does_not_kill_the_loop()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        // No DbContext registered, so the pass throws. The timer must swallow it and come back: what is due
        // is a property of the rows, so the next pass picks up everything this one missed — but only if there
        // IS a next pass. An escalation loop that died on one bad night and reported nothing is the failure
        // this whole subsystem exists to prevent, happening to the subsystem itself.
        var bare = new ServiceCollection();
        bare.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        await using var sp = bare.BuildServiceProvider();

        var sweeper = new EscalationSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EscalationSweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None);
        // The first pass has run and thrown by now; the service is still running rather than faulted.
        await Task.Delay(200);
        var stop = () => sweeper.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task Stopping_the_sweeper_ends_it_promptly_rather_than_after_the_interval()
    {
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        // The interval is five minutes. A loop that waited it out on shutdown would hold a container up long
        // past its grace period and be SIGKILLed — mid-sweep, which is survivable here only because the pass
        // is idempotent. It should still stop when asked.
        await using var sp = Services();
        var sweeper = new EscalationSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EscalationSweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None);
        var stopped = Task.Run(async () => await sweeper.StopAsync(CancellationToken.None));

        (await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().Be(stopped, "the delay is cancellable, so shutdown does not wait out the interval");
    }

    // ---------------------------------------------------------------- fixtures

    private static NotificationDbContext Ctx() =>
        new(new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    /// <summary>One actionable notification, past its escalation window and unread — the state the sweep is
    /// looking for.</summary>
    private static async Task SeedDueAsync(string tenant)
    {
        await using var db = Ctx();
        var now = DateTimeOffset.UtcNow;
        db.Set<Domain.Notification>().Add(new Domain.Notification
        {
            NotificationId = Guid.NewGuid(), TenantId = tenant,
            RecipientUserId = "u-1", RecipientRole = "medical_approval",
            Channel = NotificationChannel.InApp, Locale = "en",
            TemplateKey = "auth.approved", Subject = "s", Body = "b", StatusText = "Queued",
            SourceEventId = Guid.NewGuid(), SourceEventType = "AuthApproved",
            Status = DeliveryStatus.Queued, CreatedAt = now,
            Actionable = true,
            EscalationDueAt = now.AddHours(-1),
            EscalationToUserId = "u-supervisor",
            EscalatedAt = null,
            ReadAt = null,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string tenant)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM notification.notification WHERE tenant_id = {0}", tenant);
    }
}
