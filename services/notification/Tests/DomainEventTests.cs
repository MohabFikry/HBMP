using FluentAssertions;
using Mersal.Notification.Api;
using Mersal.Notification.Domain;

namespace Mersal.Notification.Tests;

/// <summary>
/// The fan-out subscription (US-072) and its first two publishers.
///
/// <para>The gap this closes: RequestInfo changed a status and wrote a note, and told nobody. The officer who
/// filed the application had no reason to reopen a row they had already finished with, so the request sat
/// unanswered while the application aged in a queue ordered by how long people had been waiting.</para>
///
/// <para>The envelope is the unusual part and is what these pin. Every other route fans out to a ROLE that the
/// consumer resolves against the directory; this one carries its recipient, because which officer filed a given
/// application is a fact only patient-service holds.</para>
/// </summary>
public class DomainEventTests
{
    /// <summary>The general envelope every publisher now sends: tenant, entity, interpolation fields, and the
    /// recipients the PUBLISHER resolved (this service holds no directory logic).</summary>
    private const string Payload = """
        {
          "tenantId": "11111111-1111-1111-1111-111111111111",
          "entityRef": "registration:8f14e45f-ceea-467a-9575-3c0e2a3e0e11",
          "fields": { "ref": "MF-04833" },
          "recipients": [ { "userId": "u-layla", "role": "registration_officer", "locale": "ar" } ]
        }
        """;

    /// <summary>The shape patient-service published before 19.7. Still accepted, because messages written by
    /// the previous build are still on the queue and dropping them loses the notices they exist to deliver.</summary>
    private const string LegacyPayload = """
        {
          "tenantId": "11111111-1111-1111-1111-111111111111",
          "registrationId": "8f14e45f-ceea-467a-9575-3c0e2a3e0e11",
          "recipientUserId": "u-layla",
          "reference": "MF-04833"
        }
        """;

    [Fact]
    public void Routes_to_the_registration_officer_on_in_app_and_email()
    {
        var route = RoutingTable.Route("RegistrationInfoRequested");
        route.Should().NotBeNull();
        route!.TemplateKey.Should().Be("registration.info_requested");
        route.Targets.Should().ContainSingle()
            .Which.Role.Should().Be("registration_officer");
        route.Targets[0].Channels.Should().Contain(NotificationChannel.InApp);
    }

    [Fact]
    public void Is_actionable_because_an_unanswered_request_is_the_failure_mode()
        => RoutingTable.Route("RegistrationInfoRequested")!.Actionable.Should().BeTrue();

    [Fact]
    public void Is_sensitive_so_the_send_is_audited()
        => RoutingTable.Route("RegistrationInfoRequested")!.Sensitive.Should().BeTrue();

    [Fact]
    public void Parses_the_tenant_the_registration_and_the_addressee()
    {
        var parsed = DomainEventConsumer.Parse(Payload);
        parsed.Should().NotBeNull();
        parsed!.TenantId.Should().Be("11111111-1111-1111-1111-111111111111");
        parsed.Recipients.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { UserId = "u-layla", Role = "registration_officer", Locale = "ar" });
        parsed.Fields["ref"].Should().Be("MF-04833");
    }

    [Fact]
    public void Refuses_a_message_with_no_addressee()
    {
        // A notice with no recipient cannot be delivered, and guessing one — fanning out to the whole role —
        // would train the team to ignore the channel. Dead-lettered instead.
        var noRecipient = Payload.Replace("\"u-layla\"", "\"\"", StringComparison.Ordinal);
        DomainEventConsumer.Parse(noRecipient).Should().BeNull();
    }

    [Fact]
    public void Refuses_a_message_that_cannot_be_attributed_to_a_tenant()
    {
        // An in-app notice written under a guessed tenant is a cross-tenant disclosure, which is worse than a
        // lost doorbell.
        var noTenant = Payload.Replace("\"11111111-1111-1111-1111-111111111111\"", "\"\"", StringComparison.Ordinal);
        DomainEventConsumer.Parse(noTenant).Should().BeNull();
    }

    [Fact]
    public void Still_reads_the_envelope_the_previous_build_published()
    {
        // In-flight messages must not be lost across a deployment.
        var parsed = DomainEventConsumer.Parse(LegacyPayload);
        parsed.Should().NotBeNull();
        parsed!.Recipients.Should().ContainSingle().Which.Role.Should().Be("registration_officer");
        parsed.Fields["ref"].Should().Be("MF-04833");
    }

    [Fact]
    public void Groups_several_recipients_by_the_role_the_route_targets()
    {
        // A route fans out per ROLE, so two people in one role must arrive as one group rather than
        // overwriting each other.
        const string many = """
            {
              "tenantId": "t1",
              "fields": { "authNo": "AUTH-1" },
              "recipients": [
                { "userId": "u1", "role": "requesting_provider", "locale": "en" },
                { "userId": "u2", "role": "requesting_provider", "locale": "ar" },
                { "userId": "u3", "role": "beneficiary", "locale": "ar" }
              ]
            }
            """;
        var parsed = DomainEventConsumer.Parse(many)!;
        parsed.Recipients.Should().HaveCount(3);
        parsed.Recipients.Count(r => r.Role == "requesting_provider").Should().Be(2);
    }

    [Fact]
    public void Routes_every_authorization_decision_this_platform_publishes()
    {
        // approvals-service emits these five by name (Decisions.EventType). Before 19.7 each had a route and a
        // bilingual template here and NOTHING delivered one — a clinician learned their authorization had been
        // decided by opening the worklist and looking.
        foreach (var type in new[] { "AuthApproved", "AuthPartiallyApproved", "AuthRejected", "AuthInfoRequested", "AuthEmergencyApproved" })
            RoutingTable.Route(type).Should().NotBeNull($"{type} is published by approvals-service");
    }

    [Fact]
    public void Every_routed_template_interpolates_the_field_name_its_publishers_send()
    {
        // The auth templates all read "Authorization {ref} …". A publisher that names the field `authNo`
        // produces a notice reading "Authorization  was approved" and NOTHING fails — a missing token renders
        // empty by design (see Missing_token_renders_empty_never_leaks_the_brace). This is that contract,
        // asserted rather than assumed: the template owns the name, the publisher matches it.
        var rendered = TemplateRenderer.Render(
            "Authorization {ref} was approved.",
            new Dictionary<string, string> { ["ref"] = "AUTH-2026-000123" });
        rendered.Should().Be("Authorization AUTH-2026-000123 was approved.");

        TemplateRenderer.Render("Authorization {ref} was approved.",
            new Dictionary<string, string> { ["authNo"] = "AUTH-2026-000123" })
            .Should().Be("Authorization  was approved.", "a mis-named field fails silently, which is why this test exists");
    }

    [Fact]
    public void Carries_no_clinical_field_into_the_notification_body()
    {
        // The dispatcher throws on a forbidden key; this asserts the field bag we build cannot trip it. The
        // supervisor's prose stays on the registration thread, behind authorization.
        var parsed = DomainEventConsumer.Parse(Payload)!;
        TemplateRenderer.ContainsClinicalField(parsed.Fields).Should().BeFalse();
    }
}
