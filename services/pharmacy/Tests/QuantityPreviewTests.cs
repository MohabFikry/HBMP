using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Infrastructure;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 29.6 — "how much will be dispensed?", answered before the doctor commits (design 45 §6).
///
/// <para><b>Why the SERVER answers it.</b> The composer fills its quantity field in from this rather than
/// multiplying three numbers of its own. <c>QuantityMath</c> is the one implementation of that arithmetic —
/// the validation check grades against it and the dispensing counter meters against it — and a TypeScript
/// copy in the browser would be a second answer to "how much medicine does this person get".</para>
///
/// <para><b>And why it resolves the pack facts ITSELF.</b> The same defect the chronic preview shipped with:
/// it read them from the request body, the composer had none to send, and every call answered
/// <c>quantity-not-checked</c> whatever the catalogue held. Pack facts are master data; a screen that
/// fetched them to hand back would be a second place deciding what the catalogue says.</para>
/// </summary>
[Collection("pharmacy-db")]
public class QuantityPreviewTests(PrescribingApiFactory f) : IClassFixture<PrescribingApiFactory>
{
    private async Task<HttpResponseMessage> PostAsync(object body)
        => await f.Prescriber().PostAsJsonAsync("/api/v1/prescriptions/quantity-preview", body);

    [SkippableFact]
    public async Task It_reads_the_drugs_pack_facts_itself_and_answers_with_a_NUMBER()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var drug = Guid.NewGuid();
        f.Packs[drug] = new DrugPack(IsPackSplittable: true, PackSize: 20m, PrescribingUnit: "Tablet");

        var r = await PostAsync(new { drugId = drug, doseAmount = 1m, timesPerDay = 3, durationDays = 7 });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalUnits").GetDecimal().Should().Be(21m);
        // Splittable, so the pharmacy counts out exactly 21 rather than handing over two whole boxes of 20.
        body.GetProperty("dispenseQuantity").GetDecimal().Should().Be(21m);
        // The UNIT travels with the number, so the composer can say "21 Tablet" rather than a bare 21.
        body.GetProperty("prescribingUnit").GetString().Should().Be("Tablet");
    }

    [SkippableFact]
    public async Task A_pack_that_cannot_be_split_is_counted_in_WHOLE_packs()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var drug = Guid.NewGuid();
        f.Packs[drug] = new DrugPack(IsPackSplittable: false, PackSize: 200m, PrescribingUnit: "Puff");

        var r = await PostAsync(new { drugId = drug, doseAmount = 2m, timesPerDay = 2, durationDays = 30 });

        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalUnits").GetDecimal().Should().Be(120m);
        body.GetProperty("packs").GetDecimal().Should().Be(1m);
        body.GetProperty("dispenseQuantity").GetDecimal().Should().Be(200m, "a whole inhaler leaves the counter");
    }

    [SkippableFact]
    public async Task A_drug_with_no_recorded_splittability_reports_NotChecked_NAMING_the_column()
    {
        // Invariant 8. The field is named as the master-data COLUMN because the person who fixes it reads the
        // drug table, not a JSON body — and a guessed quantity is a dispensing error that looks exactly like
        // a correct one.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var drug = Guid.NewGuid();
        f.Packs[drug] = new DrugPack(IsPackSplittable: null, PackSize: null);

        var r = await PostAsync(new { drugId = drug, doseAmount = 1m, timesPerDay = 1, durationDays = 30 });

        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("quantity-not-checked");
        body.GetProperty("detail").GetString().Should().Contain("is_pack_splittable");
    }

    [SkippableFact]
    public async Task An_incomplete_line_says_WHICH_of_the_doctors_own_numbers_is_missing()
    {
        // Distinct from a catalogue gap, and it has to be: one is fixed by typing in the next field, the
        // other by a data administrator. "Could not compute" for both sends the doctor to the wrong place.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var drug = Guid.NewGuid();
        f.Packs[drug] = new DrugPack(IsPackSplittable: true, PackSize: 20m, PrescribingUnit: "Tablet");

        var r = await PostAsync(new { drugId = drug, doseAmount = 1m, timesPerDay = 3 });   // no duration

        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString().Should().Contain("duration");
    }
}
