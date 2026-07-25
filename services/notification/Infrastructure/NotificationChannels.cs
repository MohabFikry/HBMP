using Mersal.Notification.Domain;
using Microsoft.Extensions.Logging;

namespace Mersal.Notification.Infrastructure;

/// <summary>The outcome of a channel send attempt.</summary>
public sealed record ChannelResult(DeliveryStatus Status, string? Error = null)
{
    public static ChannelResult Delivered() => new(DeliveryStatus.Delivered);
    public static ChannelResult Sent() => new(DeliveryStatus.Sent);
    public static ChannelResult Failed(string error) => new(DeliveryStatus.Failed, error);
    public static ChannelResult Skipped(string reason) => new(DeliveryStatus.Skipped, reason);
}

/// <summary>The channel extension point (07 US-072). A new channel implements this + registers itself; the
/// dispatcher is channel-agnostic. Live channels: in-app, email. Future stubs: SMS, WhatsApp (flagged off).</summary>
public interface INotificationChannel
{
    NotificationChannel Channel { get; }
    bool Enabled { get; }
    Task<ChannelResult> SendAsync(Domain.Notification notification, CancellationToken ct = default);
}

/// <summary>In-app channel: the notification row IS the delivery (the inbox reads it), so a send is immediately
/// "delivered" — persistence is the dispatcher's job. Always enabled.</summary>
public sealed class InAppChannel : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.InApp;
    public bool Enabled => true;
    public Task<ChannelResult> SendAsync(Domain.Notification notification, CancellationToken ct = default) =>
        Task.FromResult(ChannelResult.Delivered());
}

/// <summary>Email provider abstraction — swap SMTP/SendGrid/etc. behind this. The dev/default provider logs instead
/// of sending (no external dependency in Tier 1).</summary>
public interface IEmailProvider
{
    Task SendAsync(string recipientUserId, string subject, string body, string locale, CancellationToken ct = default);
}

/// <summary>Default email provider: logs the send (Tier 1 dev / tests). Replaced by a real SMTP/API provider in
/// Tier 2/3 via the same interface.</summary>
public sealed class LoggingEmailProvider(ILogger<LoggingEmailProvider> logger) : IEmailProvider
{
    public Task SendAsync(string recipientUserId, string subject, string body, string locale, CancellationToken ct = default)
    {
        logger.LogInformation("Email send (dev): to={Recipient} locale={Locale} subject={Subject}", recipientUserId, locale, subject);
        return Task.CompletedTask;
    }
}

/// <summary>Email channel over the provider abstraction. A provider exception → Failed (the dispatcher records the
/// state and the delivery-retry sweep re-attempts with backoff). "Accepted by provider" = Sent; a delivery-status
/// callback would later flip it to Delivered.</summary>
public sealed class EmailChannel(IEmailProvider provider) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Email;
    public bool Enabled => true;

    public async Task<ChannelResult> SendAsync(Domain.Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        try
        {
            await provider.SendAsync(notification.RecipientUserId, notification.Subject, notification.Body, notification.Locale, ct);
            return ChannelResult.Sent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChannelResult.Failed(ex.Message);
        }
    }
}

/// <summary>SMS future-channel STUB (07 US-072). Implements the interface so the extension point is real, but is
/// disabled by default (<see cref="NotificationOptions"/>); the dispatcher never calls a disabled channel, and if
/// asked it logs "not yet enabled" and performs NO live send.</summary>
public sealed class SmsChannel(NotificationOptions options, ILogger<SmsChannel> logger) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Sms;
    public bool Enabled => options.EnableSms;

    public Task<ChannelResult> SendAsync(Domain.Notification notification, CancellationToken ct = default)
    {
        logger.LogInformation("SMS channel not yet enabled — no live send for notification {Id}", notification?.NotificationId);
        return Task.FromResult(ChannelResult.Skipped("sms-not-enabled"));
    }
}

/// <summary>WhatsApp future-channel STUB — as SMS, disabled by default, no live send.</summary>
public sealed class WhatsAppChannel(NotificationOptions options, ILogger<WhatsAppChannel> logger) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.WhatsApp;
    public bool Enabled => options.EnableWhatsApp;

    public Task<ChannelResult> SendAsync(Domain.Notification notification, CancellationToken ct = default)
    {
        logger.LogInformation("WhatsApp channel not yet enabled — no live send for notification {Id}", notification?.NotificationId);
        return Task.FromResult(ChannelResult.Skipped("whatsapp-not-enabled"));
    }
}
