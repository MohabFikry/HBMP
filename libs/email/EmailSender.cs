using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Mersal.Email;

/// <summary>
/// Sending an email to an ADDRESS (ADR-0036 §6.3, phase 28.5).
///
/// <para>
/// ============================================================================================================
/// WHY THIS LIBRARY EXISTS
/// ============================================================================================================
/// A password reset that cannot be delivered is not a password reset. Before this, the platform's only
/// <c>IEmailProvider</c> wrote a log line and <b>returned success</b> — so a reset screen would have said
/// "if that account exists, we have sent you a link" while nothing was sent, forever, with no error anywhere.
/// That is the platform's own forbidden pattern (a failed operation rendered as a clean result) landing on
/// the one screen a locked-out person reaches when nothing else works.
/// </para>
/// <para>
/// ============================================================================================================
/// ADDRESS, NOT USER ID — AND THAT DISTINCTION IS THE FINDING
/// ============================================================================================================
/// notification-service's <c>IEmailProvider</c> takes a <i>recipient user id</i>, because the logging stub
/// never needed anywhere to send to. Its <c>Notification</c> entity stores no email address and the service
/// has no directory lookup, so an SMTP client wired in there would be a client with nowhere to send. That gap
/// is real and is NOT closed here; this interface deliberately takes an address, and identity-service — which
/// holds the address — is its first caller.
/// </para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Whether a real transport is configured.
    ///
    /// <para>
    /// Exposed so a CALLER can refuse to offer a capability it cannot deliver, rather than accepting the
    /// request and silently dropping it. "Check unavailable" is never rendered as OK, and "we emailed you"
    /// when nothing was emailed is the same lie with a friendlier face.
    /// </para>
    /// </summary>
    bool IsConfigured { get; }

    Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken ct = default);
}

public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "no-reply@mersal.local";
    public string FromName { get; set; } = "Mersal HBMP";

    /// <summary>
    /// STARTTLS by default, and <c>None</c> is a value a deployment has to ask for by name.
    ///
    /// <para>
    /// Tier 1 runs a local Mailpit over plain SMTP, which is the reason the option exists at all. Defaulting
    /// to no encryption to make that work would make every unconfigured deployment send credentials and reset
    /// links in clear text — a default that is convenient in the one place it is harmless and wrong
    /// everywhere else.
    /// </para>
    /// </summary>
    public string Security { get; set; } = "StartTls";
}

/// <summary>
/// SMTP over MailKit. Throws on failure — a caller must not be able to mistake a failed send for a sent one.
/// </summary>
public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Host);

    public async Task SendAsync(
        string toAddress, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        // Not a silent no-op. A caller that reaches here without a transport has already failed to check
        // IsConfigured, and swallowing it would produce exactly the "we sent you a link" lie this library
        // exists to prevent.
        if (!IsConfigured)
            throw new InvalidOperationException(
                "No SMTP host is configured. Check IEmailSender.IsConfigured before offering to send mail.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

        using var client = new SmtpClient();
        var security = options.Security.Trim().ToLowerInvariant() switch
        {
            "none" => SecureSocketOptions.None,
            "ssl" or "sslonconnect" => SecureSocketOptions.SslOnConnect,
            "auto" => SecureSocketOptions.Auto,
            _ => SecureSocketOptions.StartTls,
        };

        await client.ConnectAsync(options.Host, options.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(options.UserName))
            await client.AuthenticateAsync(options.UserName, options.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        // The RECIPIENT and the subject, never the body: a reset body contains a single-use credential, and
        // a log is exactly the kind of place a short-lived secret outlives its window.
        logger.LogInformation("Email sent to {Recipient} — {Subject}", toAddress, subject);
    }
}

/// <summary>
/// The sender used when nothing is configured. <see cref="IsConfigured"/> is false and every send throws.
///
/// <para>
/// Deliberately NOT a logging stub that returns success. That shape is what let a whole delivery path look
/// healthy while delivering nothing, and it is the reason this library exists.
/// </para>
/// </summary>
public sealed class UnconfiguredEmailSender : IEmailSender
{
    public bool IsConfigured => false;

    public Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken ct = default) =>
        throw new InvalidOperationException("No email transport is configured.");
}

public static class EmailServiceCollectionExtensions
{
    /// <summary>Wire the SMTP sender from <c>Email:*</c> configuration. With no <c>Email:Host</c> the
    /// registered sender reports <c>IsConfigured == false</c> and refuses to send, so a caller that asks
    /// first offers nothing it cannot do, and one that does not gets an exception rather than silence.</summary>
    public static IServiceCollection AddHbmpEmail(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = new EmailOptions();
        config.GetSection("Email").Bind(options);
        services.AddSingleton(options);
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }
}
