using FluentAssertions;
using Mersal.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mersal.Email.Tests;

/// <summary>
/// Phase 28.5 — what happens when there is no way to send.
///
/// <para>
/// The behaviour being pinned is a REFUSAL, and it is pinned because the thing it replaces did the opposite:
/// the platform's only email provider wrote a log line and returned success, so a password-reset screen would
/// have reported "we have sent you a link" while nothing was sent, forever, with no error anywhere. Every
/// test here exists to stop that shape coming back.
/// </para>
/// </summary>
public class EmailSenderTests
{
    private static SmtpEmailSender Sender(EmailOptions options) =>
        new(options, NullLogger<SmtpEmailSender>.Instance);

    [Fact]
    public void With_no_host_the_sender_reports_that_it_cannot_send()
    {
        // The flag a caller checks BEFORE offering a capability. Without it the only honest options are to
        // attempt the send and surface an error to the user, or to lie.
        Sender(new EmailOptions()).IsConfigured.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_host_is_not_a_host(string host)
    {
        // Blank, not just null. Compose and Helm both supply empty strings for unset values, and a check that
        // only tested for null would report a configured transport that cannot resolve anything.
        Sender(new EmailOptions { Host = host }).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void With_a_host_it_reports_that_it_can()
    {
        Sender(new EmailOptions { Host = "mailpit" }).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task Sending_with_no_transport_THROWS_rather_than_quietly_succeeding()
    {
        // THE test. A silent no-op here is indistinguishable from a delivered message at every call site, and
        // that is precisely how a reset flow ends up telling people to check an inbox nothing was sent to.
        var act = () => Sender(new EmailOptions())
            .SendAsync("someone@example.org", "Subject", "<p>body</p>", "body");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No SMTP host is configured*");
    }

    [Fact]
    public async Task The_unconfigured_sender_also_throws()
    {
        var act = () => new UnconfiguredEmailSender().SendAsync("a@b.c", "s", "h", "t");
        await act.Should().ThrowAsync<InvalidOperationException>();
        new UnconfiguredEmailSender().IsConfigured.Should().BeFalse();
    }

    // ---- the defaults ----------------------------------------------------------------------------------

    [Fact]
    public void Transport_security_defaults_to_StartTls()
    {
        // Tier 1 runs a local Mailpit over plain SMTP, which is the only reason the option exists. Defaulting
        // to None to make that convenient would send credentials and reset links in clear text everywhere
        // else — a default that is harmless in one place and wrong in every other.
        new EmailOptions().Security.Should().Be("StartTls");
        new EmailOptions().Port.Should().Be(587);
    }

    [Fact]
    public void Configuration_binds_the_transport()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddHbmpEmail(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Host"] = "mailpit",
            ["Email:Port"] = "1025",
            ["Email:Security"] = "None",
        }).Build());

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEmailSender>().IsConfigured.Should().BeTrue();
        provider.GetRequiredService<EmailOptions>().Port.Should().Be(1025);
    }

    [Fact]
    public void An_unconfigured_registration_yields_a_sender_that_says_so()
    {
        // Registration must never fail for want of a mail server — the issuer being unable to start because
        // no relay is configured would turn a missing convenience into an authentication outage.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHbmpEmail(new ConfigurationBuilder().Build());

        services.BuildServiceProvider().GetRequiredService<IEmailSender>().IsConfigured.Should().BeFalse();
    }
}
