using FluentAssertions;
using Mersal.Notification.Domain;

namespace Mersal.Notification.Tests;

/// <summary>Pure unit tests for the notification building blocks (US-072): bilingual template rendering with
/// min-necessary interpolation, the clinical-field guard, and the event→role→channel routing + escalation config.</summary>
public class TemplateAndRoutingTests
{
    [Fact]
    public void Renders_english_template_interpolating_non_clinical_fields()
    {
        var body = TemplateRenderer.Render("Authorization {ref} for {providerName} was approved.",
            new Dictionary<string, string> { ["ref"] = "AUTH-2026-000123", ["providerName"] = "Nile Clinic" });
        body.Should().Be("Authorization AUTH-2026-000123 for Nile Clinic was approved.");
    }

    [Fact]
    public void Renders_arabic_template_authored_not_machine_translated()
    {
        // The AR body is pre-authored; the renderer only substitutes tokens (RTL content preserved verbatim).
        var body = TemplateRenderer.Render("تمت الموافقة على التفويض {ref} الخاص بـ {providerName}.",
            new Dictionary<string, string> { ["ref"] = "AUTH-2026-000123", ["providerName"] = "عيادة النيل" });
        body.Should().Contain("عيادة النيل").And.Contain("AUTH-2026-000123");
    }

    [Fact]
    public void Missing_token_renders_empty_never_leaks_the_brace()
    {
        TemplateRenderer.Render("Hello {missing}!", new Dictionary<string, string>()).Should().Be("Hello !");
    }

    [Fact]
    public void Clinical_field_guard_flags_a_forbidden_key()
    {
        TemplateRenderer.ContainsClinicalField(new Dictionary<string, string> { ["diagnosis"] = "E11.9" }).Should().BeTrue();
        TemplateRenderer.ContainsClinicalField(new Dictionary<string, string> { ["ref"] = "AUTH-1" }).Should().BeFalse();
    }

    [Theory]
    [InlineData("AuthApproved", "auth.approved")]
    [InlineData("AuthRejected", "auth.rejected")]
    [InlineData("AuthInfoRequested", "auth.info_requested")]
    // Keyed on what orders-service actually publishes (§11.3); the TEMPLATE key is unchanged.
    [InlineData("OrderResultUploaded", "result.ready")]
    [InlineData("OrderLineAvailable", "order.line_available")]
    [InlineData("RxLineOutOfStock", "rx.out_of_stock")]
    public void Routes_known_events_to_their_template(string eventType, string templateKey)
    {
        RoutingTable.Route(eventType)!.TemplateKey.Should().Be(templateKey);
    }

    [Fact]
    public void Approval_decision_targets_the_requesting_provider_on_in_app_and_email()
    {
        var route = RoutingTable.Route("AuthApproved")!;
        var provider = route.Targets.Single(t => t.Role == "requesting_provider");
        provider.Channels.Should().Contain(NotificationChannel.InApp).And.Contain(NotificationChannel.Email);
        route.Sensitive.Should().BeTrue();
    }

    [Fact]
    public void Unknown_event_has_no_route()
    {
        RoutingTable.Route("SomethingUnmapped").Should().BeNull();
    }

    /// <summary>
    /// The names four other services actually publish (audit §11.3).
    ///
    /// <para>These four routes were keyed on a vocabulary written here and never adopted anywhere else —
    /// `ResultReady`, `RxReady`, `AppointmentReminder`, `AppointmentNoShow` — while orders, pharmacy and emr
    /// were sending `OrderResultUploaded`, `RxApproved`, `AppointmentReminderIssued` and `ApptNoShow`. Nothing
    /// failed and nothing was delivered: an event with no route is dropped with a log, so a routing table full
    /// of unreachable entries reads exactly like a working fan-out.</para>
    ///
    /// <para>Pinned by NAME rather than by behaviour because a name is the whole contract here. A rename on
    /// either side puts the fan-out back to silent, and silence is the failure mode no test catches by
    /// accident.</para>
    /// </summary>
    [Theory]
    [InlineData("OrderResultUploaded")]   // orders-service, Results.cs
    [InlineData("RxApproved")]            // pharmacy-service, Prescriptions.cs
    [InlineData("AppointmentReminderIssued")]  // emr-service, Reminders.cs
    [InlineData("ApptNoShow")]            // emr-service, Appointments.cs
    public void Routes_are_keyed_on_the_names_publishers_actually_send(string publishedEventType)
    {
        RoutingTable.Route(publishedEventType).Should().NotBeNull(
            "the fan-out is keyed on the event type off the wire, so a route under any other name is unreachable");
    }

    /// <summary>
    /// The old names must NOT resolve.
    ///
    /// <para>Keeping both would be the alias approach, and this codebase refuses it for the reason §11.2
    /// records about the audit sink and §3.1 about column headers: two names for one fact is two places to
    /// change, and they drift. A stale key that still routes lets a publisher be "fixed" to the wrong name
    /// without anything noticing.</para>
    /// </summary>
    [Theory]
    [InlineData("ResultReady")]
    [InlineData("RxReady")]
    [InlineData("AppointmentReminder")]
    [InlineData("AppointmentNoShow")]
    public void The_names_no_service_publishes_are_gone_rather_than_aliased(string retiredEventType)
    {
        RoutingTable.Route(retiredEventType).Should().BeNull();
    }

    [Fact]
    public void Info_requested_is_actionable_and_escalates_to_the_medical_director()
    {
        RoutingTable.Route("AuthInfoRequested")!.Actionable.Should().BeTrue();
        var esc = RoutingTable.Escalation("AuthInfoRequested")!;
        esc.EscalateToRole.Should().Be("medical_director");
        esc.Window.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Non_actionable_events_have_no_escalation()
    {
        RoutingTable.Escalation("AuthApproved").Should().BeNull();
    }
}
