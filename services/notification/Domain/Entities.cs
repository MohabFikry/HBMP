namespace Mersal.Notification.Domain;

/// <summary>Delivery channels. Two are live (in-app + email); SMS/WhatsApp are future-channel stubs, wired behind a
/// feature flag that is OFF by default (07 US-072). The enum lists them so the routing/registry extension point is
/// explicit; the dispatcher refuses to send on a disabled channel.</summary>
public enum NotificationChannel
{
    InApp,
    Email,
    Sms,
    WhatsApp,
}

/// <summary>Per-notification delivery lifecycle (07 US-072). <c>Skipped</c> records a channel that was requested but
/// is flagged off (SMS/WhatsApp) — proof that no live send occurred, without a false failure.</summary>
public enum DeliveryStatus
{
    Queued,
    Sent,
    Delivered,
    Failed,
    Skipped,
}

/// <summary>Authored locales. Arabic is RTL and authored, never machine-translated at send time (CLAUDE.md i18n).</summary>
public static class Locales
{
    public const string Arabic = "ar";
    public const string English = "en";
    public static bool IsSupported(string? l) => l is Arabic or English;
    public static string OrDefault(string? l) => IsSupported(l) ? l! : English;
}

/// <summary>A single persisted notification — one row per (event, recipient, channel). In-app notifications ARE this
/// row (the inbox reads them); email/sms rows track the external send. Bodies carry only min-necessary, non-clinical
/// interpolated fields (AUTH key, status text, provider name) — NEVER diagnoses or clinical detail.</summary>
public sealed class Notification
{
    public Guid NotificationId { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = default!;

    public string RecipientUserId { get; set; } = default!;
    public string RecipientRole { get; set; } = default!;
    public NotificationChannel Channel { get; set; }
    public string Locale { get; set; } = Locales.English;

    public string TemplateKey { get; set; } = default!;
    public string Subject { get; set; } = default!;
    public string Body { get; set; } = default!;
    public string StatusText { get; set; } = default!;   // canonical non-color status vocabulary (design system)

    public Guid SourceEventId { get; set; }
    public string SourceEventType { get; set; } = default!;
    public string? EntityRef { get; set; }               // e.g. AUTH-2026-000123 (min-necessary business key)
    public bool Sensitive { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Queued;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    // Escalation: an actionable notification unread past its window escalates to the next recipient. The escalation
    // target (supervisor / Medical Director) is resolved from the directory at fan-out time and captured here, so the
    // sweep needs no directory lookup.
    public bool Actionable { get; set; }
    public DateTimeOffset? EscalationDueAt { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public Guid? EscalatedFromId { get; set; }
    public string? EscalationToUserId { get; set; }
    public string? EscalationToRole { get; set; }
    public string? EscalationToLocale { get; set; }

    public bool IsActedOn => ReadAt is not null;
}

/// <summary>A versioned bilingual template record (07 US-072). Both <c>ar</c> and <c>en</c> are authored; the
/// renderer picks the recipient's preferred locale. Only the active version of a (key, locale) is rendered.</summary>
public sealed class NotificationTemplate
{
    public Guid TemplateId { get; set; } = Guid.NewGuid();
    public string TemplateKey { get; set; } = default!;
    public string Locale { get; set; } = default!;
    public int Version { get; set; } = 1;
    public bool Active { get; set; } = true;
    public string Subject { get; set; } = default!;
    public string Body { get; set; } = default!;
}

/// <summary>Dedupe ledger: a domain event id is fanned out at most once (consumers dedupe on event id,
/// 16-service-architecture.md). A redelivered event is a no-op.</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = default!;
    public int NotificationsCreated { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
}
