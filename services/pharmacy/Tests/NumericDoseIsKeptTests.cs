using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Api;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 31.5 — the numbers a prescription was WRITTEN FROM are kept, not spent.
///
/// ============================================================================================================
/// WHAT WAS BEING THROWN AWAY
/// ============================================================================================================
/// <para><c>doseAmount</c> and <c>timesPerDay</c> arrived on every line of every prescription. The daily-dose
/// rule compared against them, the quantity check divided by them, the chronic allocation split a course by
/// them — and then they were dropped. What the row kept was <c>dose</c>: a SENTENCE this application had
/// formatted, "1 Tablet x 3/day".</para>
///
/// <para>So a prescription could not be copied without retyping its dose, and could not be re-checked at all:
/// re-running a rule over a written script needs the numbers it was graded on, and the only route back to them
/// was parsing a string built for humans to read. That is the shape of defect this codebase keeps finding —
/// display text pressed into service as data.</para>
///
/// <para>These go through HTTP rather than asserting on the entity, because "the column exists" is not the
/// claim. The claim is that a number sent by a prescriber comes back to them.</para>
/// </summary>
[Collection("prescribing-api")]
public class NumericDoseIsKeptTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task The_dose_and_frequency_a_prescriber_sent_come_back_on_the_line()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            using var doctor = app.Prescriber();
            var created = await Submit(doctor, app, doseAmount: 1.5m, timesPerDay: 3, durationDays: 7);
            created.StatusCode.Should().Be(HttpStatusCode.Created);

            var line = (await created.Content.ReadFromJsonAsync<JsonElement>(Web))
                .GetProperty("lines").EnumerateArray().First();

            line.GetProperty("doseAmount").GetDecimal().Should().Be(1.5m,
                "half a tablet is a dose somebody wrote, and rounding it away changes the prescription");
            line.GetProperty("timesPerDay").GetInt32().Should().Be(3);
            line.GetProperty("durationDays").GetInt32().Should().Be(7);
            // The sig is still there and still the sentence a pharmacist reads. These do not replace it.
            line.GetProperty("dose").GetString().Should().NotBeNullOrWhiteSpace();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_line_written_WITHOUT_them_reports_null_rather_than_one()
    {
        // Invariant 8, in the one place a tidy default is most tempting: a dose of 1 and a frequency of 1 look
        // like an ordinary once-daily script. They would be a prescription nobody wrote, and every check
        // downstream would grade against them without knowing.
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            using var doctor = app.Prescriber();
            var created = await Submit(doctor, app, doseAmount: null, timesPerDay: null, durationDays: 7);
            created.StatusCode.Should().Be(HttpStatusCode.Created);

            var line = (await created.Content.ReadFromJsonAsync<JsonElement>(Web))
                .GetProperty("lines").EnumerateArray().First();

            line.GetProperty("doseAmount").ValueKind.Should().Be(JsonValueKind.Null);
            line.GetProperty("timesPerDay").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task They_survive_an_amendment_onto_the_new_version()
    {
        /*
         * An amendment supersedes a line with a new version carrying ONE changed number — the quantity. The
         * dose and frequency are the same clinical instruction and must arrive unaltered on the successor,
         * or the amended script becomes the one row in the record whose numbers were lost.
         */
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            using var doctor = app.Prescriber();
            var created = await Submit(doctor, app, doseAmount: 2m, timesPerDay: 2, durationDays: 10);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>(Web);
            var rxId = body.GetProperty("prescriptionId").GetGuid();
            var lineId = body.GetProperty("lines").EnumerateArray().First()
                .GetProperty("prescriptionLineId").GetGuid();

            var amend = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri($"/api/v1/prescriptions/{rxId}/lines/{lineId}/amend", UriKind.Relative))
            {
                Content = JsonContent.Create(
                    new { quantityPrescribed = 20m, reasonCode = "PrescribingError" }, options: Web),
            };
            amend.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var amended = await doctor.SendAsync(amend);
            amended.StatusCode.Should().Be(HttpStatusCode.OK);

            var after = await doctor.GetAsync(new Uri($"/api/v1/prescriptions/{rxId}", UriKind.Relative));
            var successor = (await after.Content.ReadFromJsonAsync<JsonElement>(Web))
                .GetProperty("lines").EnumerateArray()
                .First(l => l.GetProperty("quantityPrescribed").GetDecimal() == 20m);

            successor.GetProperty("doseAmount").GetDecimal().Should().Be(2m);
            successor.GetProperty("timesPerDay").GetInt32().Should().Be(2);
        }
        finally { await app.CleanupAsync(); }
    }

    private static async Task<HttpResponseMessage> Submit(
        HttpClient client, PrescribingApiFactory app,
        decimal? doseAmount, int? timesPerDay, int? durationDays)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/prescriptions", UriKind.Relative))
        {
            Content = JsonContent.Create(new CreatePrescriptionRequest(
                app.Beneficiary, app.Encounter, null,
                AcknowledgeAlerts: false,
                Lines: [new CreateRxLine(app.DrugA, "500mg", "PO", "BD", 14, 0,
                    DurationDays: durationDays, ClientLineId: Guid.NewGuid(),
                    DoseAmount: doseAmount, TimesPerDay: timesPerDay)],
                DiagnosisIcdCodes: ["E11.9"],
                Acknowledgements: []), options: Web),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }
}
