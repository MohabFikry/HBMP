using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Authz;
using Xunit;

namespace Mersal.CallCentre.Tests;

/// <summary>
/// 21.4 — the third gate through a REAL service host (design 40 §4).
///
/// The unit tests in libs/authz prove the filter's logic; this proves the WIRING: that
/// <c>UseProgramFeature</c> sits in callcentre-service's pipeline in the right place, so a fully authorized
/// agent whose organisation is not on the contact-centre programme is refused — and refused with the problem
/// type that sends them to Mersal rather than to their own administrator.
///
/// It also pins the ORDER. The gate must be the LAST question: a caller who lacks the scope must still get the
/// authorization denial, because "your organisation is not enabled" is the wrong answer to "you do not have this
/// permission" and would send them to the wrong people entirely.
/// </summary>
[Collection("callcentre-db")]
public class ProgramGateTests
{
    private static void Authorize(HttpClient c, string? features)
    {
        c.DefaultRequestHeaders.Add("X-Test-Sub", "33333333-3333-3333-3333-333333333333");
        c.DefaultRequestHeaders.Add("X-Test-Role", "call_center");
        c.DefaultRequestHeaders.Add("X-Test-Scope", "callcentre:interaction callcentre:verify callcentre:read callcentre:act");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "t-callcentre");
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        c.DefaultRequestHeaders.Add("X-Test-Features", features ?? "");
    }

    [Fact]
    public async Task An_agent_whose_tenant_is_not_on_the_programme_is_refused()
    {
        Skip.If(CallCentreFactory.Db is null, "CALLCENTRE_TEST_DB not set — DB integration test skipped.");
        using var factory = new CallCentreFactory();
        var client = factory.CreateClient();
        // Authorized in every other respect, and on OTHER programmes — just not this one.
        Authorize(client, ProgramFeatures.Emr + " " + ProgramFeatures.Claims);

        var response = await client.PostAsJsonAsync(
            "/api/v1/call-interactions", new { direction = "Inbound", reasonCode = "BookAppointment" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = problem.RootElement;
        root.GetProperty("type").GetString().Should().Be(
            ProgramEnablement.NotEnabledType,
            "the remedy is 'ask Mersal to enable the programme', not 'ask your administrator for the permission'");
        root.GetProperty("code").GetString().Should().Be(ProgramEnablement.NotEnabledCode);
        root.GetProperty("feature").GetString().Should().Be(ProgramFeatures.CallCentre);
    }

    [Fact]
    public async Task The_same_agent_on_the_programme_is_admitted()
    {
        Skip.If(CallCentreFactory.Db is null, "CALLCENTRE_TEST_DB not set — DB integration test skipped.");
        using var factory = new CallCentreFactory();
        var client = factory.CreateClient();
        Authorize(client, ProgramFeatures.CallCentre);

        var response = await client.PostAsJsonAsync(
            "/api/v1/call-interactions", new { direction = "Inbound", reasonCode = "BookAppointment" });

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// ORDER. This caller is on the programme but holds none of the callcentre scopes. The answer must be the
    /// authorization denial — NOT `program-not-enabled`, which would tell them to contact Mersal about something
    /// their own tenant administrator controls.
    /// </summary>
    [Fact]
    public async Task A_caller_lacking_the_scope_gets_the_authorization_denial_not_the_programme_one()
    {
        Skip.If(CallCentreFactory.Db is null, "CALLCENTRE_TEST_DB not set — DB integration test skipped.");
        using var factory = new CallCentreFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", "44444444-4444-4444-4444-444444444444");
        client.DefaultRequestHeaders.Add("X-Test-Role", "reception");
        client.DefaultRequestHeaders.Add("X-Test-Scope", "reception:read");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "t-callcentre");
        client.DefaultRequestHeaders.Add("X-Test-Features", ProgramFeatures.CallCentre);

        var response = await client.PostAsJsonAsync(
            "/api/v1/call-interactions", new { direction = "Inbound", reasonCode = "BookAppointment" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(
            ProgramEnablement.NotEnabledCode,
            "enablement is asked LAST — the authorization refusal must reach the caller unchanged");
    }

    /// <summary>Health probes are anonymous, so the gate must not touch them: a disabled module's container has
    /// to stay alive and reporting, not fall over.</summary>
    [Fact]
    public async Task Liveness_stays_reachable_for_a_tenant_with_the_programme_off()
    {
        Skip.If(CallCentreFactory.Db is null, "CALLCENTRE_TEST_DB not set — DB integration test skipped.");
        using var factory = new CallCentreFactory();
        var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
