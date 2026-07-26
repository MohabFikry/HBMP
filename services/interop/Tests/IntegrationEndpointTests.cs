using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Interop.Domain.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Interop.Tests;

/// <summary>End-to-end 13.2 tests through the real host + registry DB (env-gated <c>INTEROP_TEST_DB</c>): the DPIA
/// gate refuses enablement until a DPIA + data-sharing agreement are recorded; inbound ingest quarantines a
/// disabled partner and maps an enabled one through the anti-corruption layer. Self-cleans the shared registry.</summary>
public class IntegrationEndpointTests(InteropFactory factory) : IClassFixture<InteropFactory>
{
    private const string Referral = "digital-referral-network";

    [SkippableFact]
    public async Task Partners_list_includes_the_seeded_roadmap_partners()
    {
        Skip.If(InteropFactory.Db is null, "INTEROP_TEST_DB not configured.");
        var admin = factory.ClientFor("super_admin", "admin:read", "admin:write");
        var resp = await admin.GetAsync("/interop/integration/partners");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().Contain(Referral).And.Contain("unhcr-identity");
    }

    [SkippableFact]
    public async Task Enable_is_refused_until_dpia_and_agreement_then_inbound_maps()
    {
        Skip.If(InteropFactory.Db is null, "INTEROP_TEST_DB not configured.");
        var admin = factory.ClientFor("super_admin", "admin:read", "admin:write");
        await ResetAsync(Referral);
        try
        {
            // 1. Enable refused — no DPIA/agreement.
            var refused = await admin.PostAsync($"/interop/integration/partners/{Referral}/enable", Empty());
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("dpia-gate-blocked");

            // 2. Record DPIA + data-sharing agreement.
            var dpia = await admin.PostAsJsonAsync($"/interop/integration/partners/{Referral}/dpia",
                new { dataSharingAgreementRef = "DSA-2026-777", crossBorder = false });
            dpia.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3. Now enable succeeds.
            var enabled = await admin.PostAsync($"/interop/integration/partners/{Referral}/enable", Empty());
            enabled.StatusCode.Should().Be(HttpStatusCode.OK);
            (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Enabled");

            // 4. Inbound valid referral → mapped through the ACL.
            var inbound = await admin.PostAsJsonAsync($"/interop/integration/inbound/{Referral}", new
            {
                format = "fhir+json",
                body = """{ "resourceType": "ServiceRequest", "intent": "order", "subject": { "reference": "Patient/MRS-M-9" }, "code": { "coding": [ { "code": "394579002" } ] } }""",
            });
            inbound.StatusCode.Should().Be(HttpStatusCode.OK);
            (await inbound.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString().Should().Be("Mapped");

            // The refusal + enable + ingest were all audited.
            factory.Audit.Events.Should().Contain(e => e.EntityType == "integration-partner" && e.DecisionOutcome == "enable-refused");
            factory.Audit.Events.Should().Contain(e => e.EntityType == "integration-partner" && e.DecisionOutcome == "enabled");
        }
        finally { await ResetAsync(Referral); }
    }

    [SkippableFact]
    public async Task Inbound_to_a_disabled_partner_is_quarantined()
    {
        Skip.If(InteropFactory.Db is null, "INTEROP_TEST_DB not configured.");
        var admin = factory.ClientFor("super_admin", "admin:read", "admin:write");
        await ResetAsync("unhcr-identity");

        var inbound = await admin.PostAsJsonAsync("/interop/integration/inbound/unhcr-identity",
            new { format = "batch", body = "anything" });
        inbound.StatusCode.Should().Be(HttpStatusCode.OK);
        (await inbound.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString().Should().Be("Quarantined");
    }

    [SkippableFact]
    public async Task Non_admin_cannot_list_or_enable()
    {
        Skip.If(InteropFactory.Db is null, "INTEROP_TEST_DB not configured.");
        var doctor = factory.ClientFor("doctor", "fhir:read:Patient");
        (await doctor.GetAsync("/interop/integration/partners")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await doctor.PostAsync($"/interop/integration/partners/{Referral}/enable", Empty())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static StringContent Empty() => new("", Encoding.UTF8, "application/json");

    /// <summary>Reset a shared partner back to Disabled / NotStarted / no-agreement so tests don't contaminate.</summary>
    private async Task ResetAsync(string partnerId)
    {
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IExternalPartnerRegistry>();
        var p = await registry.GetAsync(partnerId);
        if (p is null) return;
        await registry.UpsertAsync(p with { Status = IntegrationStatus.Disabled, Dpia = DpiaStatus.NotStarted, DataSharingAgreementRef = null });
    }
}
