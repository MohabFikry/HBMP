using Mersal.Notification.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Notification.Infrastructure;

/// <summary>Inbox mutations that are more than a single-row edit, kept out of the endpoint lambdas so the
/// recipient row-filter they depend on can be tested directly against the datastore.</summary>
public static class InboxOperations
{
    /// <summary>Marks every unread in-app notification belonging to <paramref name="recipientUserId"/> read, in one
    /// transaction, and returns how many rows that actually changed. Self-service by construction: the filter is
    /// the caller's own id, so no other recipient's inbox is reachable. Idempotent — a second call marks 0.</summary>
    public static async Task<int> MarkAllReadAsync(
        NotificationDbContext db, string recipientUserId, DateTimeOffset now, CancellationToken ct = default)
    {
        var unread = await db.Notifications
            .Where(n => n.RecipientUserId == recipientUserId
                        && n.Channel == NotificationChannel.InApp
                        && n.ReadAt == null)
            .ToListAsync(ct);
        if (unread.Count == 0) return 0;

        foreach (var n in unread) n.ReadAt = now;
        await db.SaveChangesAsync(ct);
        return unread.Count;
    }
}
