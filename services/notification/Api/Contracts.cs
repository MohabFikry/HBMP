using Mersal.Notification.Domain;
using Mersal.Notification.Infrastructure;

namespace Mersal.Notification.Api;

/// <summary>The fan-out seam payload (system → notification-service). Carries the routed domain event, its
/// min-necessary NON-clinical interpolation fields, and the role→recipient resolution done by the routing consumer.
/// No clinical payload crosses this boundary.</summary>
public sealed record IngestRequest(
    Guid EventId,
    string EventType,
    string TenantId,
    string? EntityRef,
    Dictionary<string, string> Fields,
    Dictionary<string, List<RecipientDto>> RoleRecipients)
{
    public NotificationEnvelope ToEnvelope() => new(
        EventId, EventType, TenantId, EntityRef,
        Fields ?? [],
        (RoleRecipients ?? []).ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<Recipient>)kv.Value.Select(r => new Recipient(r.UserId, r.Locale)).ToList()));
}

public sealed record RecipientDto(string UserId, string Locale);

public sealed record IngestResultView(bool Deduplicated, int Created);

/// <summary>In-app inbox item — min-necessary: no source PHI, only the rendered non-clinical notice.</summary>
public sealed record InboxItemView(
    Guid NotificationId,
    string StatusText,
    string Subject,
    string Body,
    string Locale,
    string? EntityRef,
    string SourceEventType,
    bool Read,
    DateTimeOffset CreatedAt)
{
    public static InboxItemView From(Domain.Notification n) =>
        new(n.NotificationId, n.StatusText, n.Subject, n.Body, n.Locale, n.EntityRef, n.SourceEventType,
            n.ReadAt is not null, n.CreatedAt);
}

/// <summary>Result of clearing the caller's unread inbox — how many notifications this call actually marked.</summary>
public sealed record MarkAllReadView(int Marked);

/// <summary>Per-notification delivery state.</summary>
public sealed record DeliveryView(
    Guid NotificationId,
    string Channel,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset? SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? FailedAt)
{
    public static DeliveryView From(Domain.Notification n) =>
        new(n.NotificationId, n.Channel.ToString(), n.Status.ToString(), n.Attempts, n.LastError,
            n.SentAt, n.DeliveredAt, n.FailedAt);
}
