using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Api;
using Mersal.Validity;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// Every prescription carries an end date.
/// </summary>
/// <remarks>
/// <para>
/// <c>expires_at</c> has been in migration 0001 since the beginning and the dispensing rule has always
/// honoured it. Nothing ever WROTE it — so every prescription this platform had issued was valid for ever,
/// and the whole expiry mechanism sat there looking implemented. That is the failure mode these tests exist
/// for: a column, a rule and an enum value that all agree with each other and are never reached.
/// </para>
/// <para>
/// The period comes from configuration, so the assertions below pin the DEFAULT (ten days) rather than a
/// hard-coded date — the fixture injects <c>DefaultValidityPolicySource</c>. What is deliberately NOT stubbed
/// out is the stamping itself.
/// </para>
/// </remarks>
[Collection("prescribing-api")]
public class PrescriptionExpiryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task A_submitted_prescription_expires_after_the_configured_period()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            using var doctor = app.Prescriber();
            var created = await Submit(doctor, app, requestedExpiry: null);
            created.StatusCode.Should().Be(HttpStatusCode.Created);

            var body = await created.Content.ReadFromJsonAsync<JsonElement>(Web);
            var expiresAt = body.GetProperty("expiresAt");

            expiresAt.ValueKind.Should().NotBe(JsonValueKind.Null,
                "a prescription with no end date is a clinical decision nobody ever revisits");

            var expiry = expiresAt.GetDateTimeOffset();
            var expected = ValidityPolicy.ExpiryFor(DateTimeOffset.UtcNow, ValidityPolicy.DefaultDays);
            expiry.Should().BeCloseTo(expected, TimeSpan.FromMinutes(1));
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_client_may_shorten_the_validity_but_never_extend_it()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            using var doctor = app.Prescriber();

            // A three-day course: the prescriber genuinely means this to lapse sooner, and may say so.
            var shorter = DateTimeOffset.UtcNow.AddDays(3);
            var shortRx = await Submit(doctor, app, shorter);
            shortRx.StatusCode.Should().Be(HttpStatusCode.Created);
            (await shortRx.Content.ReadFromJsonAsync<JsonElement>(Web))
                .GetProperty("expiresAt").GetDateTimeOffset()
                .Should().BeCloseTo(shorter, TimeSpan.FromSeconds(5));

            // A year: this is a caller helping themselves to a validity the Medical Director did not grant.
            // The request body is not where that authority lives, so it is clamped rather than refused —
            // the prescription is still written, at the period the tenant actually set.
            var longer = DateTimeOffset.UtcNow.AddDays(365);
            var longRx = await Submit(doctor, app, longer);
            longRx.StatusCode.Should().Be(HttpStatusCode.Created);

            var granted = (await longRx.Content.ReadFromJsonAsync<JsonElement>(Web))
                .GetProperty("expiresAt").GetDateTimeOffset();
            granted.Should().BeBefore(longer);
            granted.Should().BeCloseTo(
                ValidityPolicy.ExpiryFor(DateTimeOffset.UtcNow, ValidityPolicy.DefaultDays), TimeSpan.FromMinutes(1));
        }
        finally { await app.CleanupAsync(); }
    }

    [Fact]
    public void The_counter_reads_expiry_from_the_clock_not_from_the_status()
    {
        var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

        // Lapsed a week ago, but the hourly sweeper has not reached it — the ROW still says Approved. A
        // screen that trusted the status would show "Approved" on something the server refuses to dispense,
        // which is the worst of both: the pharmacist is told to hand it over and then cannot.
        var notYetSwept = new Domain.Prescription
        {
            PrescriptionId = Guid.NewGuid(), RxNo = "RX-2026-000001",
            Status = Domain.RxStatus.Approved,
            ExpiresAt = now.AddDays(-7),
            Lines = [],
        };

        DispensableRxView.From(notYetSwept, now).Expired.Should().BeTrue();

        // And the converse — a live prescription is not marked expired just because it has a date.
        var live = new Domain.Prescription
        {
            PrescriptionId = Guid.NewGuid(), RxNo = "RX-2026-000002",
            Status = Domain.RxStatus.Approved,
            ExpiresAt = now.AddDays(3),
            Lines = [],
        };

        DispensableRxView.From(live, now).Expired.Should().BeFalse();
    }

    private static async Task<HttpResponseMessage> Submit(
        HttpClient client, PrescribingApiFactory app, DateTimeOffset? requestedExpiry)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/prescriptions", UriKind.Relative))
        {
            Content = JsonContent.Create(new CreatePrescriptionRequest(
                app.Beneficiary, app.Encounter, requestedExpiry,
                AcknowledgeAlerts: false,
                Lines: [new CreateRxLine(app.DrugA, "500mg", "PO", "BD", 14, 0,
                    DurationDays: 7, ClientLineId: Guid.NewGuid())],
                DiagnosisIcdCodes: ["E11.9"],
                Acknowledgements: []), options: Web),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }
}
