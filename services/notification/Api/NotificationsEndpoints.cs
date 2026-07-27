using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Notification.Domain;
using Mersal.Notification.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Notification.Api;

/// <summary>Phase 8.1 endpoints (US-072): the per-user in-app inbox (min-necessary), delivery status, mark-read
/// (which also stops escalation), and the system fan-out seam. Inbox/delivery/read are strictly self-service —
/// row-filtered by recipient == caller.</summary>
public static class NotificationsEndpoints
{
    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/notifications");

        // In-app inbox — the caller's own InApp notifications, newest first. No source PHI.
        v1.MapGet("/", async (NotificationDbContext db, NotificationGate gate, IHbmpPrincipalAccessor me,
            bool? unreadOnly, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(NotificationPolicies.Read, ct);
            if (denied is not null) return denied;

            var uid = me.Principal!.Subject;
            var q = db.Notifications.AsNoTracking()
                .Where(n => n.RecipientUserId == uid && n.Channel == NotificationChannel.InApp);
            if (unreadOnly == true) q = q.Where(n => n.ReadAt == null);

            var items = await q.OrderByDescending(n => n.CreatedAt).Take(200)
                .Select(n => InboxItemView.From(n)).ToListAsync(ct);
            return Results.Ok(items);
        }).RequireAuthorization(HbmpPolicies.Scope("notification:read"));

        // Delivery status of one of the caller's own notifications.
        v1.MapGet("/{id:guid}/delivery", async (Guid id, NotificationDbContext db, NotificationGate gate,
            IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(NotificationPolicies.Read, ct);
            if (denied is not null) return denied;

            var uid = me.Principal!.Subject;
            var n = await db.Notifications.AsNoTracking()
                .FirstOrDefaultAsync(x => x.NotificationId == id && x.RecipientUserId == uid, ct);
            return n is null ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found") : Results.Ok(DeliveryView.From(n));
        }).RequireAuthorization(HbmpPolicies.Scope("notification:read"));

        // Mark read — acting on the notification (stops its escalation timer).
        v1.MapPost("/{id:guid}/read", async (Guid id, NotificationDbContext db, NotificationGate gate,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(NotificationPolicies.Read, ct);
            if (denied is not null) return denied;

            var uid = me.Principal!.Subject;
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.NotificationId == id && x.RecipientUserId == uid, ct);
            if (n is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (n.ReadAt is null) { n.ReadAt = clock.GetUtcNow(); await db.SaveChangesAsync(ct); }
            return Results.Ok(InboxItemView.From(n));
        }).RequireAuthorization(HbmpPolicies.Scope("notification:read"));

        // Mark ALL of the caller's unread in-app notifications read, in one transaction. Self-service like the
        // single-item route (row-filtered by recipient == caller), and idempotent: a second call marks nothing
        // and reports 0. Done server-side rather than by the client looping the per-id route, so clearing a
        // full inbox is one request and one commit instead of up to 200 of each.
        v1.MapPost("/read-all", async (NotificationDbContext db, NotificationGate gate,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(NotificationPolicies.Read, ct);
            if (denied is not null) return denied;

            var marked = await InboxOperations.MarkAllReadAsync(db, me.Principal!.Subject, clock.GetUtcNow(), ct);
            return Results.Ok(new MarkAllReadView(marked));
        }).RequireAuthorization(HbmpPolicies.Scope("notification:read"));

        // The fan-out seam — a routed domain event drives notification creation + dispatch (idempotent on event id).
        v1.MapPost("/ingest", async (IngestRequest req, NotificationDispatcher dispatcher, NotificationGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(NotificationPolicies.Ingest, ct);
            if (denied is not null) return denied;

            var result = await dispatcher.DispatchAsync(req.ToEnvelope(), ct);
            return Results.Ok(new IngestResultView(result.Deduplicated, result.Created));
        }).RequireAuthorization(HbmpPolicies.Scope("notification:ingest"));
    }
}
