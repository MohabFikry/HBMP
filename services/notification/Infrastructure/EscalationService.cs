using Mersal.Notification.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mersal.Notification.Infrastructure;

/// <summary>Time-based escalation (07 US-072): an actionable notification (InfoRequested, SLA breach, out-of-stock)
/// not acted on within its window escalates to the configured next recipient (supervisor / Medical Director). The
/// escalation target was resolved + captured at fan-out time, so the sweep needs no directory lookup. Idempotent:
/// a notification escalates at most once (guarded by <c>escalated_at</c>). Wired to a scheduled sweep in Tier 2/3;
/// invoked directly in tests.</summary>
public sealed class EscalationService(
    NotificationDbContext db,
    IEnumerable<INotificationChannel> channels,
    TimeProvider clock,
    ILogger<EscalationService> logger)
{
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationChannel> _channels =
        channels.ToDictionary(c => c.Channel);

    /// <summary>Escalate all due, unacted, not-yet-escalated actionable notifications. Returns the count escalated.</summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.Notifications
            .Where(n => n.Actionable
                        && n.ReadAt == null
                        && n.EscalatedAt == null
                        && n.EscalationDueAt != null && n.EscalationDueAt <= now
                        && n.EscalationToUserId != null)
            .ToListAsync(ct);

        // A single actionable event may have produced several channel rows (e.g. in-app + email) for the same
        // recipient — they escalate together to ONE notification for the target, not one per channel (which would
        // collide on the (event, recipient, channel) unique index). Group by (event, escalation target).
        var escalated = new List<Domain.Notification>();
        foreach (var group in due.GroupBy(n => (n.SourceEventId, n.EscalationToUserId)))
        {
            var anchor = group.First();
            foreach (var n in group) n.EscalatedAt = now;

            // Re-use the original template/body — same min-necessary, non-clinical content — re-targeted to the
            // supervisor with an "Escalated" status text on the in-app channel.
            var esc = new Domain.Notification
            {
                TenantId = anchor.TenantId,
                RecipientUserId = anchor.EscalationToUserId!,
                RecipientRole = anchor.EscalationToRole ?? "supervisor",
                Channel = NotificationChannel.InApp,
                Locale = anchor.EscalationToLocale ?? anchor.Locale,
                TemplateKey = anchor.TemplateKey,
                Subject = anchor.Subject,
                Body = anchor.Body,
                StatusText = "Escalated",
                SourceEventId = anchor.SourceEventId,
                SourceEventType = anchor.SourceEventType,
                EntityRef = anchor.EntityRef,
                Sensitive = anchor.Sensitive,
                Actionable = false,       // the escalation itself does not re-escalate
                CreatedAt = now,
                EscalatedFromId = anchor.NotificationId,
                Status = DeliveryStatus.Delivered,
                DeliveredAt = now,
                SentAt = now,
                Attempts = 1,
            };
            escalated.Add(esc);
            logger.LogInformation("Escalated notification {Id} → {Role} ({User})", anchor.NotificationId, esc.RecipientRole, esc.RecipientUserId);
        }

        if (escalated.Count > 0)
        {
            db.Notifications.AddRange(escalated);
            await db.SaveChangesAsync(ct);
        }
        return escalated.Count;
    }
}
