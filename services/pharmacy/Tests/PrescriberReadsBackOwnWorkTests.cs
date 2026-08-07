using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Api;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// A prescriber can read back the prescriptions they wrote.
/// </summary>
/// <remarks>
/// <para>Sounds too obvious to test, and was broken in production the whole time. <c>GET
/// /prescriptions/mine</c> — the list the encounter's Prescriptions tab reads, filtered on
/// <c>CreatedBy == sub</c> — required <c>pharmacy:read</c>, the DISPENSER's scope. A doctor holds
/// <c>rx:write</c> and <c>rx:read</c> and never that one, so every prescription they submitted disappeared
/// the instant it was saved: the POST returned 201, the row was in the database, and the tab that should
/// have shown it got a 403 and rendered an empty list.</para>
///
/// <para>Nothing caught it because the test fixture handed the prescriber <c>pharmacy:read</c> as well —
/// a fixture more generous than the issuer, which tests a system nobody runs. The fixture now carries
/// exactly the issuer's doctor scopes, which is what makes the first test below meaningful rather than
/// decorative.</para>
/// </remarks>
[Collection("prescribing-api")]
public class PrescriberReadsBackOwnWorkTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task A_prescriber_sees_the_prescription_they_just_wrote()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            using var doctor = app.Prescriber();

            var created = await Submit(doctor, app);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var rxNo = (await created.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("rxNo").GetString();

            // The round trip the encounter screen makes. With `pharmacy:read` on this endpoint it was a 403.
            var mine = await doctor.GetAsync(new Uri("/api/v1/prescriptions/mine", UriKind.Relative));
            mine.StatusCode.Should().Be(HttpStatusCode.OK,
                "rx:read is the prescriber's own-work scope; pharmacy:read belongs to the dispenser");

            var rows = await mine.Content.ReadFromJsonAsync<JsonElement>(Web);
            rows.EnumerateArray().Select(r => r.GetProperty("rxNo").GetString())
                .Should().Contain(rxNo, "a doctor who cannot see their own prescription cannot tell it saved");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_list_is_refused_without_the_prescriber_read_scope()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            // The complement: widening the scope is not the fix, so prove the endpoint is still gated. A
            // caller who can WRITE a prescription but holds no read scope is refused the list.
            using var writeOnly = app.Prescriber(scopes: "rx:write");

            (await writeOnly.GetAsync(new Uri("/api/v1/prescriptions/mine", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    private static async Task<HttpResponseMessage> Submit(HttpClient client, PrescribingApiFactory app)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/prescriptions", UriKind.Relative))
        {
            Content = JsonContent.Create(new CreatePrescriptionRequest(
                app.Beneficiary, app.Encounter, null,
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
