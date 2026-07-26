using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Notification.Domain;
using Mersal.Notification.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mersal.Notification.Tests;

/// <summary>Fan-out engine at the datastore (env-gated <c>NOTIFICATION_TEST_DB</c>, seeded templates). Proves
/// US-072: a relevant event fans out to the correct role on in-app AND email in the recipient's locale with no
/// clinical payload; a redelivered event creates exactly one set (idempotent); a failed email retries with the
/// delivery state reflecting the outcome; an unacted actionable notification escalates on the timer; a disabled
/// SMS channel performs no live send; and a sensitive-context send is audited. Serialized via notification-db.</summary>
[Collection("notification-db")]
public class NotificationDispatchTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("NOTIFICATION_TEST_DB");

    private static DbContextOptions<NotificationDbContext> Options() =>
        new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    /// <summary>A test clock we can advance to fire backoff / escalation windows deterministically.</summary>
    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>An email provider that fails a configurable number of times, then succeeds — for the retry test.</summary>
    private sealed class FlakyEmailProvider(int failures) : IEmailProvider
    {
        private int _remaining = failures;
        public int Sends { get; private set; }
        public Task SendAsync(string r, string s, string b, string l, CancellationToken ct = default)
        {
            if (_remaining-- > 0) throw new InvalidOperationException("smtp unavailable");
            Sends++;
            return Task.CompletedTask;
        }
    }

    private static NotificationEnvelope Envelope(string eventType, string entityRef, TenantAndRecipients r) =>
        new(Guid.NewGuid(), eventType, "t0", entityRef, r.Fields, r.RoleRecipients);

    private sealed record TenantAndRecipients(
        IReadOnlyDictionary<string, string> Fields,
        IReadOnlyDictionary<string, IReadOnlyList<Recipient>> RoleRecipients);

    private static (NotificationDispatcher, InMemoryAuditOutbox, IEmailProvider) Build(
        NotificationDbContext db, TimeProvider clock, IEmailProvider? email = null, NotificationOptions? opts = null)
    {
        var audit = new InMemoryAuditOutbox();
        email ??= new NullEmail();
        var options = opts ?? new NotificationOptions();
        var channels = new INotificationChannel[]
        {
            new InAppChannel(), new EmailChannel(email),
            new SmsChannel(options, NullLogger<SmsChannel>.Instance),
            new WhatsAppChannel(options, NullLogger<WhatsAppChannel>.Instance),
        };
        var dispatcher = new NotificationDispatcher(db, channels, options,
            new AuditClient(audit, new AuditClientContext("notification-test"), clock), clock,
            NullLogger<NotificationDispatcher>.Instance);
        return (dispatcher, audit, email);
    }

    private sealed class NullEmail : IEmailProvider
    {
        public Task SendAsync(string r, string s, string b, string l, CancellationToken ct = default) => Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Approval_decision_fans_out_to_the_provider_on_in_app_and_email_in_locale_with_no_clinical_payload()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = "prov-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var db = new NotificationDbContext(Options());
            var (dispatcher, _, _) = Build(db, TimeProvider.System);

            var env = new NotificationEnvelope(Guid.NewGuid(), "AuthApproved", "t0", "AUTH-2026-000999",
                new Dictionary<string, string> { ["ref"] = "AUTH-2026-000999", ["providerName"] = "Nile Clinic" },
                new Dictionary<string, IReadOnlyList<Recipient>>
                {
                    ["requesting_provider"] = [new(provider, "ar")],
                    ["beneficiary"] = [new("ben-" + provider, "en")],
                });

            var result = await dispatcher.DispatchAsync(env);
            result.Created.Should().Be(3); // provider in-app+email (2) + beneficiary in-app (1)

            var rows = await db.Notifications.AsNoTracking().Where(n => n.SourceEventId == env.EventId).ToListAsync();
            rows.Should().HaveCount(3);
            rows.Should().Contain(n => n.RecipientUserId == provider && n.Channel == NotificationChannel.InApp);
            rows.Should().Contain(n => n.RecipientUserId == provider && n.Channel == NotificationChannel.Email);
            // The provider's locale (ar) was honored, and the AR body interpolated the business key — no diagnosis.
            var arInApp = rows.Single(n => n.RecipientUserId == provider && n.Channel == NotificationChannel.InApp);
            arInApp.Locale.Should().Be("ar");
            arInApp.Body.Should().Contain("AUTH-2026-000999").And.NotContainAny("diagnosis", "E11");
        }
        finally { await Cleanup(provider); }
    }

    [SkippableFact]
    public async Task A_redelivered_event_creates_exactly_one_set_of_notifications()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = "prov-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var eventId = Guid.NewGuid();
            var env = new NotificationEnvelope(eventId, "ResultReady", "t0", "ORD-1",
                new Dictionary<string, string> { ["ref"] = "ORD-1" },
                new Dictionary<string, IReadOnlyList<Recipient>> { ["ordering_doctor"] = [new(provider, "en")] });

            await using (var db = new NotificationDbContext(Options()))
            {
                var (d1, _, _) = Build(db, TimeProvider.System);
                (await d1.DispatchAsync(env)).Created.Should().Be(1);
            }
            await using (var db = new NotificationDbContext(Options()))
            {
                var (d2, _, _) = Build(db, TimeProvider.System);
                var replay = await d2.DispatchAsync(env);      // same event id redelivered
                replay.Deduplicated.Should().BeTrue();
                replay.Created.Should().Be(0);
            }

            await using var verify = new NotificationDbContext(Options());
            (await verify.Notifications.CountAsync(n => n.SourceEventId == eventId)).Should().Be(1);
        }
        finally { await Cleanup(provider); }
    }

    [SkippableFact]
    public async Task A_failed_email_retries_with_backoff_and_the_delivery_state_reflects_the_outcome()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = "prov-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var clock = new MutableClock(DateTimeOffset.UtcNow);
            var flaky = new FlakyEmailProvider(failures: 1); // first send throws, retry succeeds
            var opts = new NotificationOptions { RetryBaseSeconds = 10, MaxEmailAttempts = 4 };

            await using var db = new NotificationDbContext(Options());
            var (dispatcher, _, _) = Build(db, clock, flaky, opts);

            // rx.out_of_stock routes an email to the ordering doctor.
            var env = new NotificationEnvelope(Guid.NewGuid(), "RxLineOutOfStock", "t0", "RX-1",
                new Dictionary<string, string> { ["ref"] = "RX-1" },
                new Dictionary<string, IReadOnlyList<Recipient>>
                {
                    ["ordering_doctor"] = [new(provider, "en")],
                    ["pharmacy_supervisor"] = [new("sup-" + provider, "en")],
                });
            await dispatcher.DispatchAsync(env);

            var email = await db.Notifications.AsNoTracking()
                .FirstAsync(n => n.SourceEventId == env.EventId && n.Channel == NotificationChannel.Email);
            email.Status.Should().Be(DeliveryStatus.Failed);
            email.Attempts.Should().Be(1);

            // Retry before backoff elapses → nothing happens.
            (await dispatcher.RetryFailedEmailAsync()).Should().Be(0);

            // Advance past the backoff window → retry succeeds, state flips to Sent.
            clock.Advance(TimeSpan.FromSeconds(30));
            (await dispatcher.RetryFailedEmailAsync()).Should().Be(1);

            var after = await db.Notifications.AsNoTracking().FirstAsync(n => n.NotificationId == email.NotificationId);
            after.Status.Should().Be(DeliveryStatus.Sent);
            after.Attempts.Should().Be(2);
        }
        finally { await Cleanup(provider); }
    }

    [SkippableFact]
    public async Task An_unacted_actionable_notification_escalates_on_the_timer()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = "prov-" + Guid.NewGuid().ToString("N")[..8];
        var director = "dir-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var clock = new MutableClock(DateTimeOffset.UtcNow);
            await using var db = new NotificationDbContext(Options());
            var (dispatcher, _, _) = Build(db, clock);

            var env = new NotificationEnvelope(Guid.NewGuid(), "AuthInfoRequested", "t0", "AUTH-1",
                new Dictionary<string, string> { ["ref"] = "AUTH-1" },
                new Dictionary<string, IReadOnlyList<Recipient>>
                {
                    ["requesting_provider"] = [new(provider, "en")],
                    ["medical_director"] = [new(director, "ar")],
                });
            await dispatcher.DispatchAsync(env);

            var esc = new EscalationService(db, new INotificationChannel[] { new InAppChannel() }, clock,
                NullLogger<EscalationService>.Instance);

            // Before the 24h window: nothing escalates.
            (await esc.SweepAsync()).Should().Be(0);

            // After the window with the notification still unread: it escalates to the Director.
            clock.Advance(TimeSpan.FromHours(25));
            (await esc.SweepAsync()).Should().Be(1);

            var escalated = await db.Notifications.AsNoTracking()
                .FirstAsync(n => n.RecipientUserId == director && n.EscalatedFromId != null);
            escalated.RecipientRole.Should().Be("medical_director");
            escalated.StatusText.Should().Be("Escalated");

            // Idempotent: a second sweep does not re-escalate.
            (await esc.SweepAsync()).Should().Be(0);
        }
        finally { await Cleanup(provider); await Cleanup(director); }
    }

    [SkippableFact]
    public async Task A_disabled_sms_channel_performs_no_live_send()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        // Route a channel set that includes SMS by dispatching directly through the SMS stub — proves no live send.
        var opts = new NotificationOptions { EnableSms = false };
        var sms = new SmsChannel(opts, NullLogger<SmsChannel>.Instance);
        sms.Enabled.Should().BeFalse();
        var result = await sms.SendAsync(new Domain.Notification { NotificationId = Guid.NewGuid() });
        result.Status.Should().Be(DeliveryStatus.Skipped);
        result.Error.Should().Be("sms-not-enabled");
        await Task.CompletedTask;
    }

    [SkippableFact]
    public async Task A_sensitive_context_send_is_audited()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var provider = "prov-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var db = new NotificationDbContext(Options());
            var (dispatcher, audit, _) = Build(db, TimeProvider.System);

            var env = new NotificationEnvelope(Guid.NewGuid(), "AuthRejected", "t0", "AUTH-2",
                new Dictionary<string, string> { ["ref"] = "AUTH-2", ["providerName"] = "Nile Clinic" },
                new Dictionary<string, IReadOnlyList<Recipient>> { ["requesting_provider"] = [new(provider, "en")] });
            await dispatcher.DispatchAsync(env);

            audit.Events.Should().Contain(e => e.EntityType == "notification" && e.Purpose == "NOTIFY");
        }
        finally { await Cleanup(provider); }
    }

    private static async Task Cleanup(string userId)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = new NotificationDbContext(Options());
        var rows = await db.Notifications.Where(n => n.RecipientUserId == userId).ToListAsync();
        // also remove processed_event rows for these events so re-runs start clean
        var eventIds = rows.Select(r => r.SourceEventId).Distinct().ToList();
        db.Notifications.RemoveRange(rows);
        var pe = await db.ProcessedEvents.Where(p => eventIds.Contains(p.EventId)).ToListAsync();
        db.ProcessedEvents.RemoveRange(pe);
        await db.SaveChangesAsync();
    }
}
