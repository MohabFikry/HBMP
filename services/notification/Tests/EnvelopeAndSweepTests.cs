using FluentAssertions;
using Mersal.Notification.Api;
using Mersal.Notification.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Notification.Tests;

/// <summary>
/// The two pieces of the notification Api layer that were reachable only through a broker.
///
/// <para><b>Why these exist.</b> `services/notification:Api` fell to 60.1% against an 85% floor when
/// `DomainEventConsumer` and `EscalationSweeper` were added — 300 lines into a 550-line layer — with tests
/// covering only the parse seam. The envelope shaping and the per-tenant sweep are the parts with actual
/// decisions in them, and both fail QUIETLY: the wrong grouping sends one message per recipient instead of
/// one per role, a lost dedupe notifies the same person twice for one event, and a sweep that binds the
/// wrong tenant escalates nothing while reporting success.</para>
/// </summary>
public class EnvelopeAndSweepTests
{
    private static DomainEventConsumer.Notice Notice(params (string User, string Role, string Locale)[] recipients) =>
        new("t-1", "authorization:abc",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ref"] = "AUTH-2026-0001" },
            [.. recipients.Select(r => new DomainEventConsumer.Addressee(r.User, r.Role, r.Locale))]);

    [Fact]
    public void RECIPIENTS_ARE_GROUPED_BY_ROLE()
    {
        // The dispatcher renders ONE message per role, from that role's template. Handing it a flat list
        // would send one per person — the same notice several times, each looking deliberate.
        var e = DomainEventConsumer.BuildEnvelope(Guid.NewGuid(), "AuthApproved",
            Notice(("u1", "requesting_provider", "ar"), ("u2", "requesting_provider", "en"), ("u3", "pharmacist", "ar")));

        e.RoleRecipients.Should().HaveCount(2);
        e.RoleRecipients["requesting_provider"].Select(r => r.UserId).Should().BeEquivalentTo(["u1", "u2"]);
        e.RoleRecipients["pharmacist"].Select(r => r.UserId).Should().BeEquivalentTo(["u3"]);
    }

    [Fact]
    public void AND_DE_DUPLICATED_BY_USER_WITHIN_A_ROLE()
    {
        // A publisher that names the same person twice — easily done when two upstream rows resolve to one
        // user — must not put two identical notices in their inbox for one event.
        var e = DomainEventConsumer.BuildEnvelope(Guid.NewGuid(), "AuthApproved",
            Notice(("u1", "requesting_provider", "ar"), ("u1", "requesting_provider", "en")));

        e.RoleRecipients["requesting_provider"].Should().HaveCount(1);
        e.RoleRecipients["requesting_provider"][0].UserId.Should().Be("u1");
    }

    [Fact]
    public void The_same_user_in_TWO_roles_is_kept_in_both()
    {
        // Dedupe is WITHIN a role, not across the envelope: someone who is both the requester and the
        // pharmacist is owed the message each role receives, and collapsing them would silently drop one.
        var e = DomainEventConsumer.BuildEnvelope(Guid.NewGuid(), "AuthApproved",
            Notice(("u1", "requesting_provider", "ar"), ("u1", "pharmacist", "ar")));

        e.RoleRecipients.Should().HaveCount(2);
        e.RoleRecipients["requesting_provider"].Should().ContainSingle();
        e.RoleRecipients["pharmacist"].Should().ContainSingle();
    }

    [Fact]
    public void The_envelope_carries_the_ids_the_dispatcher_dedupes_and_renders_on()
    {
        var id = Guid.NewGuid();
        var e = DomainEventConsumer.BuildEnvelope(id, "AuthRejected", Notice(("u1", "requesting_provider", "en")));

        e.EventId.Should().Be(id, "the dispatcher dedupes a redelivery on it");
        e.EventType.Should().Be("AuthRejected", "the routing table keys on it");
        e.TenantId.Should().Be("t-1");
        e.EntityRef.Should().Be("authorization:abc");
        e.Fields["ref"].Should().Be("AUTH-2026-0001", "the token every auth template interpolates");
    }

    [Fact]
    public void An_envelope_with_no_recipients_is_shaped_rather_than_thrown()
    {
        // The receive path refuses a notice with nobody to tell BEFORE this runs; shaping an empty one must
        // still be total, so a future caller cannot turn a dead-letter into an unhandled exception.
        var e = DomainEventConsumer.BuildEnvelope(Guid.NewGuid(), "AuthApproved", Notice());
        e.RoleRecipients.Should().BeEmpty();
    }

    // ---- the sweep ---------------------------------------------------------------------------------------

    private static readonly string? Db = Environment.GetEnvironmentVariable("NOTIFICATION_TEST_DB");

    private static NotificationDbContext Ctx() =>
        new(new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    [SkippableFact]
    public async Task THE_TENANT_PROBE_FINDS_A_TENANT_WITH_A_DUE_ESCALATION()
    {
        // The sweep visits exactly the tenants this returns. If its predicate and EscalationService's ever
        // disagree, a tenant with due escalations is never visited — and the sweep reports success, because
        // it did everything it was asked to. Nothing alarms; escalations simply stop happening.
        //
        // Seeded rather than asserted against whatever the database happens to hold: an empty result would
        // otherwise "pass" while proving only that the query parses.
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        var tenant = "t-esc-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await SeedAsync(tenant, due: true);

            await using var db = Ctx();
            var tenants = await EscalationSweeper.PendingTenantsAsync(db, CancellationToken.None);

            tenants.Should().Contain(tenant, "it is actionable, unread, un-escalated and past its due time");
        }
        finally { await CleanupAsync(tenant); }
    }

    [SkippableFact]
    public async Task AND_IGNORES_ONE_THAT_HAS_ALREADY_BEEN_READ()
    {
        // The negation, and the one that matters: without it the test above passes against a probe that
        // returns every tenant it can see, which would make the sweep do a full pass per tenant per interval.
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        var tenant = "t-esc-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await SeedAsync(tenant, due: false);

            await using var db = Ctx();
            (await EscalationSweeper.PendingTenantsAsync(db, CancellationToken.None))
                .Should().NotContain(tenant, "a notice the recipient has already acted on cannot escalate");
        }
        finally { await CleanupAsync(tenant); }
    }

    /// <summary>One notification for <paramref name="tenant"/>. `due` decides whether it can escalate: the
    /// only difference is ReadAt, which is what "the recipient acted on it" means.</summary>
    private static async Task SeedAsync(string tenant, bool due)
    {
        await using var db = Ctx();
        var now = DateTimeOffset.UtcNow;
        db.Set<Mersal.Notification.Domain.Notification>().Add(new Mersal.Notification.Domain.Notification
        {
            NotificationId = Guid.NewGuid(), TenantId = tenant,
            RecipientUserId = "u-1", RecipientRole = "medical_approval",
            Channel = Mersal.Notification.Domain.NotificationChannel.InApp, Locale = "en",
            TemplateKey = "auth.approved", Subject = "s", Body = "b", StatusText = "Queued",
            SourceEventId = Guid.NewGuid(), SourceEventType = "AuthApproved",
            Status = Mersal.Notification.Domain.DeliveryStatus.Queued, CreatedAt = now,
            Actionable = true,
            EscalationDueAt = now.AddHours(-1),          // already due
            EscalationToUserId = "u-supervisor",
            EscalatedAt = null,
            ReadAt = due ? null : now,                    // acted on ⇒ nothing to escalate
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string tenant)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM notification.notification WHERE tenant_id = {0}", tenant);
    }

    [SkippableFact]
    public async Task The_probe_projects_a_tenant_id_and_nothing_else()
    {
        // Min-necessary applies to a maintenance query too: a background pass with no principal must not pull
        // a subject or a body into memory.
        //
        // Scans the SQL LITERAL, not the method's surroundings. The first version sliced from the first
        // mention of the method name — which is its CALL SITE — and so read the doc comment explaining "no
        // subject, no body", and failed on its own explanation. That is the third time in this codebase a
        // scanner has been steered by prose; the fix is always to narrow to the code.
        Skip.If(Db is null, "test DB not configured — set NOTIFICATION_TEST_DB to run this DB integration test.");
        var src = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "services", "notification", "Api", "EscalationSweeper.cs"));

        var start = src.IndexOf("SELECT DISTINCT tenant_id", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the probe's SQL must be findable, or this test reads nothing");
        var sql = src[start..src.IndexOf("\"\"\"", start, StringComparison.Ordinal)];

        sql.Should().Contain("tenant_id");
        sql.Should().NotContain("subject", "a maintenance projection has no business reading a notice's subject");
        sql.Should().NotContain("body", "nor its body");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
