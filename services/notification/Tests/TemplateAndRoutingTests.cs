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
    [InlineData("ResultReady", "result.ready")]
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
