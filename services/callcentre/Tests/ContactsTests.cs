using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Tests;

/// <summary>Pure contact-validation rules (phase 15.4).</summary>
public class ContactValidationTests
{
    [Theory]
    [InlineData("Phone", "+201001234567", true)]
    [InlineData("Phone", "abc", false)]
    [InlineData("Email", "amal@example.com", true)]
    [InlineData("Email", "not-an-email", false)]
    [InlineData("Address", "12 Nile St", true)]
    [InlineData("Address", "", false)]
    public void Validates_by_kind(string kind, string value, bool expected) =>
        ContactValidation.IsValid(kind, value).Should().Be(expected);
}

/// <summary>Real-endpoint tests for 15.4 (env-gated): a verified caller corrects a contact (delegates + audits);
/// an invalid value is 422 before anything is forwarded; unverified is 403; and booking FROM a referral sets
/// appointmentType=Referral so the existing ReferralScheduled event fires.</summary>
[Collection("callcentre-db")]
public class ContactsEndpointTests(CallCentreFactory factory) : IClassFixture<CallCentreFactory>
{
    private async Task<Guid> OpenAndVerifyAsync(HttpClient client, Guid ben)
    {
        var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound", reasonCode = "UpdateContact" });
        var id = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();
        await client.PostAsJsonAsync($"/api/v1/call-interactions/{id}/verification",
            new { beneficiaryId = ben, verifiedIdentifierTypes = new[] { "MemberNo", "Phone" }, result = "Passed" });
        return id;
    }

    [Fact]
    public async Task Verified_contact_correction_is_delegated()
    {
        if (CallCentreFactory.Db is null) return;
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var interactionId = await OpenAndVerifyAsync(client, ben);
            var resp = await client.PatchAsync($"/api/v1/call-centre/members/{ben}/contacts/{Guid.NewGuid()}",
                JsonContent.Create(new { interactionId, kind = "Phone", value = "+201009998888" }));
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            factory.Contacts.LastValue.Should().Be("+201009998888");
        }
        finally { await CleanAsync(); }
    }

    [Fact]
    public async Task Invalid_phone_is_422_before_delegation()
    {
        if (CallCentreFactory.Db is null) return;
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var interactionId = await OpenAndVerifyAsync(client, ben);
            var resp = await client.PatchAsync($"/api/v1/call-centre/members/{ben}/contacts/{Guid.NewGuid()}",
                JsonContent.Create(new { interactionId, kind = "Phone", value = "nope" }));
            resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally { await CleanAsync(); }
    }

    [Fact]
    public async Task Unverified_contact_edit_is_403()
    {
        if (CallCentreFactory.Db is null) return;
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound" });
            var interactionId = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();
            var resp = await client.PatchAsync($"/api/v1/call-centre/members/{ben}/contacts/{Guid.NewGuid()}",
                JsonContent.Create(new { interactionId, kind = "Phone", value = "+201009998888" }));
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await CleanAsync(); }
    }

    [Fact]
    public async Task Booking_from_a_referral_sets_type_Referral()
    {
        if (CallCentreFactory.Db is null) return;
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var interactionId = await OpenAndVerifyAsync(client, ben);
            var resp = await client.PostAsJsonAsync("/api/v1/call-centre/appointments",
                new { interactionId, beneficiaryId = ben, slotId = Guid.NewGuid(), appointmentType = "Consultation", referralRef = "REF-2026-000007" });
            resp.StatusCode.Should().Be(HttpStatusCode.Created);
            factory.Gateway.LastBookAppointmentType.Should().Be("Referral");
        }
        finally { await CleanAsync(); }
    }

    private static async Task CleanAsync()
    {
        await using var db = new CallCentreDbContext(
            new DbContextOptionsBuilder<CallCentreDbContext>().UseNpgsql(CallCentreFactory.Db).UseSnakeCaseNamingConvention().Options);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.appointment_link WHERE tenant_id = 't-callcentre';");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = 't-callcentre';");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = 't-callcentre';");
    }
}
