using Mersal.Audit.Client;
using Mersal.Notification.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mersal.Notification.Infrastructure;

/// <summary>One resolved recipient of a notification.</summary>
public sealed record Recipient(string UserId, string Locale);

/// <summary>The enriched fan-out envelope handed to the dispatcher: a routed domain event with its interpolation
/// fields (min-necessary, NON-clinical) and role→recipient resolution. In production the routing consumer builds
/// this from the raw domain event + the identity/provider directory (deferred with the fanout bus, see README); in
/// dev/tests the seam endpoint accepts it directly, so the dispatcher stays free of directory logic.</summary>
public sealed record NotificationEnvelope(
    Guid EventId,
    string EventType,
    string TenantId,
    string? EntityRef,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<Recipient>> RoleRecipients);

/// <summary>The outcome of a fan-out: how many notifications were created (0 if the event was a duplicate or has no
/// route).</summary>
public sealed record DispatchResult(bool Deduplicated, int Created);

/// <summary>The event-driven fan-out engine (phase 8.1). Given a routed domain event it: dedupes on event id
/// (redelivery is a no-op); looks up the routing entry; for each role→channel target renders the recipient's-locale
/// bilingual template with min-necessary NON-clinical fields; persists a notification row per (recipient, channel);
/// dispatches to live channels (disabled SMS/WhatsApp are Skipped, never sent); tracks delivery state; captures the
/// escalation window + resolved target for actionable events; and audits sends of sensitive-context notifications.</summary>
public sealed class NotificationDispatcher(
    NotificationDbContext db,
    IEnumerable<INotificationChannel> channels,
    NotificationOptions options,
    IAuditClient audit,
    TimeProvider clock,
    ILogger<NotificationDispatcher> logger)
{
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationChannel> _channels =
        channels.ToDictionary(c => c.Channel);

    public async Task<DispatchResult> DispatchAsync(NotificationEnvelope env, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(env);

        // Dedupe on event id (at-least-once delivery → exactly-once fan-out).
        if (await db.ProcessedEvents.AnyAsync(p => p.EventId == env.EventId, ct))
            return new DispatchResult(Deduplicated: true, Created: 0);

        var route = RoutingTable.Route(env.EventType);
        var now = clock.GetUtcNow();
        if (route is null)
        {
            logger.LogInformation("No route for event {EventType} ({EventId}) — ignored", env.EventType, env.EventId);
            await MarkProcessed(env, 0, now, ct);
            return new DispatchResult(false, 0);
        }

        // Defensive clinical-field guard: a notification field bag must never carry PHI (11-permission-matrix).
        if (TemplateRenderer.ContainsClinicalField(env.Fields))
            throw new InvalidOperationException($"Domain.Notification fields for {env.EventType} contain a forbidden clinical key.");

        var escalation = route.Actionable ? RoutingTable.Escalation(env.EventType) : null;
        var escalationTarget = escalation is not null
            && env.RoleRecipients.TryGetValue(escalation.EscalateToRole, out var esc) && esc.Count > 0
                ? esc[0] : null;

        var created = new List<Domain.Notification>();
        foreach (var target in route.Targets)
        {
            if (!env.RoleRecipients.TryGetValue(target.Role, out var recipients) || recipients.Count == 0)
                continue;

            foreach (var recipient in recipients)
            foreach (var channel in target.Channels)
            {
                var n = await BuildAsync(env, route, escalation, escalationTarget, target.Role, recipient, channel, now, ct);
                if (n is not null) created.Add(n);
            }
        }

        db.Notifications.AddRange(created);
        await MarkProcessed(env, created.Count, now, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same event won the race on the processed_event PK / unique index —
            // treat as the no-op it is (idempotent fan-out).
            if (await db.ProcessedEvents.AsNoTracking().AnyAsync(p => p.EventId == env.EventId, ct))
                return new DispatchResult(Deduplicated: true, Created: 0);
            throw;
        }

        // Audit sends of sensitive-context notifications (19-audit-strategy). Body carries no clinical payload; the
        // audit records that a sensitive-context notice was sent, to whom, for which entity.
        foreach (var n in created.Where(x => x.Sensitive))
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "notification", EntityId = n.NotificationId.ToString(), Action = AuditAction.Create,
                TenantId = n.TenantId, AfterState = n.Status.ToString(), Purpose = "NOTIFY",
                DecisionReasonCode = n.SourceEventType, Severity = AuditSeverity.Notice,
            }, ct);

        return new DispatchResult(false, created.Count);
    }

    private async Task<Domain.Notification?> BuildAsync(
        NotificationEnvelope env, NotificationRoute route, EscalationRule? escalation, Recipient? escalationTarget,
        string role, Recipient recipient, NotificationChannel channel, DateTimeOffset now, CancellationToken ct)
    {
        var locale = Locales.OrDefault(recipient.Locale);
        var template = await ResolveTemplateAsync(route.TemplateKey, locale, ct);
        if (template is null)
        {
            logger.LogWarning("No template {Key} for locale {Locale} — recipient {User} skipped", route.TemplateKey, locale, recipient.UserId);
            return null;
        }

        var n = new Domain.Notification
        {
            TenantId = env.TenantId,
            RecipientUserId = recipient.UserId,
            RecipientRole = role,
            Channel = channel,
            Locale = locale,
            TemplateKey = route.TemplateKey,
            Subject = TemplateRenderer.Render(template.Subject, env.Fields),
            Body = TemplateRenderer.Render(template.Body, env.Fields),
            StatusText = route.StatusText,
            SourceEventId = env.EventId,
            SourceEventType = env.EventType,
            EntityRef = env.EntityRef,
            Sensitive = route.Sensitive,
            Actionable = route.Actionable,
            CreatedAt = now,
        };

        if (route.Actionable && escalation is not null && escalationTarget is not null)
        {
            n.EscalationDueAt = now + escalation.Window;
            n.EscalationToUserId = escalationTarget.UserId;
            n.EscalationToRole = escalation.EscalateToRole;
            n.EscalationToLocale = Locales.OrDefault(escalationTarget.Locale);
        }

        await SendAsync(n, ct);
        return n;
    }

    /// <summary>Dispatch a built notification to its channel and record the delivery state. Disabled channels
    /// (SMS/WhatsApp stubs) are Skipped with a log — no live send.</summary>
    private async Task SendAsync(Domain.Notification n, CancellationToken ct)
    {
        n.Attempts++;
        if (!_channels.TryGetValue(n.Channel, out var channel) || !channel.Enabled)
        {
            logger.LogInformation("Channel {Channel} not enabled — notification {Id} skipped (no live send)", n.Channel, n.NotificationId);
            n.Status = DeliveryStatus.Skipped;
            n.LastError = $"{n.Channel}-not-enabled";
            return;
        }

        var result = await channel.SendAsync(n, ct);
        Apply(n, result, clock.GetUtcNow());
    }

    private static void Apply(Domain.Notification n, ChannelResult result, DateTimeOffset now)
    {
        n.Status = result.Status;
        n.LastError = result.Error;
        switch (result.Status)
        {
            case DeliveryStatus.Delivered: n.DeliveredAt = now; n.SentAt ??= now; break;
            case DeliveryStatus.Sent: n.SentAt = now; break;
            case DeliveryStatus.Failed: n.FailedAt = now; break;
        }
    }

    /// <summary>Re-attempt failed email deliveries whose backoff has elapsed (capped exponential). Returns the number
    /// re-attempted. Wired to a scheduled sweep in Tier 2/3; invoked directly in tests.</summary>
    public async Task<int> RetryFailedEmailAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.Notifications
            .Where(n => n.Channel == NotificationChannel.Email && n.Status == DeliveryStatus.Failed && n.Attempts < options.MaxEmailAttempts)
            .ToListAsync(ct);

        var retried = 0;
        foreach (var n in due)
        {
            if (n.FailedAt is { } f && f + options.Backoff(n.Attempts) > now) continue; // backoff not elapsed
            n.Attempts++;
            var channel = _channels[NotificationChannel.Email];
            Apply(n, await channel.SendAsync(n, ct), clock.GetUtcNow());
            retried++;
        }
        if (retried > 0) await db.SaveChangesAsync(ct);
        return retried;
    }

    private async Task<NotificationTemplate?> ResolveTemplateAsync(string key, string locale, CancellationToken ct)
    {
        var t = await db.Templates.AsNoTracking()
            .Where(x => x.TemplateKey == key && x.Locale == locale && x.Active)
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        // Fall back to English only if the preferred locale has no authored template (should not happen — both authored).
        if (t is null && locale != Locales.English)
            t = await db.Templates.AsNoTracking()
                .Where(x => x.TemplateKey == key && x.Locale == Locales.English && x.Active)
                .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        return t;
    }

    private async Task MarkProcessed(NotificationEnvelope env, int count, DateTimeOffset now, CancellationToken ct)
    {
        db.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = env.EventId, EventType = env.EventType, NotificationsCreated = count, ConsumedAt = now,
        });
        await Task.CompletedTask;
    }
}
