namespace Mersal.Notification.Infrastructure;

/// <summary>notification-service configuration. SMS/WhatsApp are OFF by default (future-channel stubs, 07 US-072);
/// email delivery retries with capped exponential backoff.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    public bool EnableSms { get; set; }
    public bool EnableWhatsApp { get; set; }

    /// <summary>Max email delivery attempts before the notification is left Failed.</summary>
    public int MaxEmailAttempts { get; set; } = 4;

    /// <summary>Base backoff (seconds) for the email retry sweep; attempt N waits base * 2^(N-1).</summary>
    public int RetryBaseSeconds { get; set; } = 30;

    public TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(RetryBaseSeconds * Math.Pow(2, Math.Max(0, attempt - 1)));
}
