using FluentAssertions;
using Mersal.Notification.Domain;
using Mersal.Notification.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Notification.Tests;

/// <summary>Bulk "mark all read" at the datastore (env-gated <c>NOTIFICATION_TEST_DB</c>). The inbox is
/// self-service, so the property that matters is the recipient row-filter: clearing one user's unread inbox must
/// leave every other recipient's untouched. Also proves the operation is idempotent and leaves already-read rows
/// (and their original read timestamps) alone. Serialized via notification-db.</summary>
[Collection("notification-db")]
public class InboxOperationsTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("NOTIFICATION_TEST_DB");

    private static DbContextOptions<NotificationDbContext> Options() =>
        new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    private static Domain.Notification Row(string user, NotificationChannel channel, DateTimeOffset? readAt) => new()
    {
        TenantId = "t0",
        RecipientUserId = user,
        RecipientRole = "medical_approval",
        Channel = channel,
        TemplateKey = "auth.submitted",
        Subject = "Authorization awaiting review",
        Body = "A new authorization is on your worklist.",
        StatusText = "Action needed",
        SourceEventId = Guid.NewGuid(),
        SourceEventType = "AuthorizationSubmitted",
        EntityRef = "AUTH-1",
        CreatedAt = DateTimeOffset.UtcNow,
        ReadAt = readAt,
    };

    [SkippableFact]
    public async Task Mark_all_read_clears_only_the_callers_unread_in_app_inbox_and_is_idempotent()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var me = "me-" + Guid.NewGuid().ToString("N")[..8];
        var other = "other-" + Guid.NewGuid().ToString("N")[..8];
        var alreadyRead = DateTimeOffset.UtcNow.AddHours(-3);
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var db = new NotificationDbContext(Options());
            db.Notifications.AddRange(
                Row(me, NotificationChannel.InApp, null),
                Row(me, NotificationChannel.InApp, null),
                Row(me, NotificationChannel.InApp, alreadyRead),
                // The email row is a delivery record, not an inbox item — the pane never shows it, so it stays unread.
                Row(me, NotificationChannel.Email, null),
                Row(other, NotificationChannel.InApp, null));
            await db.SaveChangesAsync();

            (await InboxOperations.MarkAllReadAsync(db, me, now)).Should().Be(2);

            var mine = await db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == me).ToListAsync();
            mine.Where(n => n.Channel == NotificationChannel.InApp).Should().OnlyContain(n => n.ReadAt != null);
            mine.Single(n => n.Channel == NotificationChannel.Email).ReadAt.Should().BeNull();
            // An already-read row keeps its original timestamp — the sweep must not restamp history.
            mine.Should().ContainSingle(n =>
                n.ReadAt != null && n.ReadAt.Value.ToUnixTimeSeconds() == alreadyRead.ToUnixTimeSeconds());

            // Another recipient's inbox is unreachable through my call.
            var theirs = await db.Notifications.AsNoTracking().SingleAsync(n => n.RecipientUserId == other);
            theirs.ReadAt.Should().BeNull();

            // Idempotent: nothing left to mark.
            (await InboxOperations.MarkAllReadAsync(db, me, now.AddMinutes(1))).Should().Be(0);
        }
        finally { await Cleanup(me); await Cleanup(other); }
    }

    private static async Task Cleanup(string userId)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = new NotificationDbContext(Options());
        var rows = await db.Notifications.Where(n => n.RecipientUserId == userId).ToListAsync();
        db.Notifications.RemoveRange(rows);
        await db.SaveChangesAsync();
    }
}
