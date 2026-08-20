using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Infrastructure;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 29.5 (design 45 §5) — the two reads the COMPOSER needs before a doctor can write a chronic script.
///
/// <para>The submit path was wired by the phase-30 fix, but a prescriber still had no way to reach it: the
/// SPA could not list the refill frequencies (a supervisor-configurable master table that nothing exposed)
/// and could not show the window schedule before submitting. Gate 5 asks for both in as many words — "the
/// frequency combobox" and "show the computed window schedule with per-window quantities BEFORE submit, so
/// the doctor sees 34/33/33 and can adjust".</para>
///
/// <para><b>The preview is computed by the SERVER, on purpose.</b> Re-implementing largest-remainder in
/// TypeScript would fork the one piece of arithmetic in this phase that must not be forked: the preview
/// would drift from what is actually written, and the doctor would be shown a schedule the pharmacy never
/// honours. It calls the same <c>ChronicAllocation.Plan</c> and <c>WindowSchedule.Build</c> the write path
/// calls, so "what I was shown" and "what was stored" cannot disagree.</para>
/// </summary>
[Collection("pharmacy-db")]
public class ChronicComposerSupportTests(PrescribingApiFactory f) : IClassFixture<PrescribingApiFactory>
{
    private static List<JsonElement> Arr(JsonElement e) => [.. e.EnumerateArray()];

    // ---- the frequency master table ----------------------------------------------------------------

    [SkippableFact]
    public async Task The_refill_frequencies_are_readable_so_the_composer_can_offer_them()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var r = await f.Prescriber().GetAsync("/api/v1/refill-frequencies");

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = Arr(await r.Content.ReadFromJsonAsync<JsonElement>());
        rows.Should().Contain(x => x.GetProperty("code").GetString() == "Monthly");
        rows.Should().Contain(x => x.GetProperty("code").GetString() == "Every3Months");
        // The cadence in MONTHS is what the window count is derived from; a label alone would leave the
        // composer unable to explain the schedule it is showing.
        rows.Single(x => x.GetProperty("code").GetString() == "Every3Months")
            .GetProperty("months").GetInt32().Should().Be(3);
    }

    [SkippableFact]
    public async Task An_inactive_frequency_is_not_offered()
    {
        // `Every6Months` is seeded INACTIVE by migration 0012. Offering it would let a doctor compose a
        // script the write path then refuses — the composer must not know a vocabulary the server rejects.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var rows = Arr(await (await f.Prescriber().GetAsync("/api/v1/refill-frequencies"))
            .Content.ReadFromJsonAsync<JsonElement>());

        rows.Should().NotContain(x => x.GetProperty("code").GetString() == "Every6Months");
    }

    [SkippableFact]
    public async Task The_frequencies_carry_both_languages()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var rows = Arr(await (await f.Prescriber().GetAsync("/api/v1/refill-frequencies"))
            .Content.ReadFromJsonAsync<JsonElement>());

        rows.Should().OnlyContain(x => x.GetProperty("nameEn").GetString()!.Length > 0);
        rows.Should().OnlyContain(x => x.GetProperty("nameAr").GetString()!.Length > 0);
    }

    // ---- the schedule preview ----------------------------------------------------------------------

    private async Task<JsonElement> PreviewAsync(object body)
        => await (await f.Prescriber().PostAsJsonAsync("/api/v1/prescriptions/chronic-preview", body))
            .Content.ReadFromJsonAsync<JsonElement>();

    [SkippableFact]
    public async Task The_preview_shows_the_worked_case_from_the_design_doc()
    {
        // Design 45 §5's own example: 90 days, monthly, 1 tablet three times daily -> 3 windows of 90.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var p = await PreviewAsync(new
        {
            durationDays = 90, refillFrequencyCode = "Monthly",
            doseAmount = 1m, timesPerDay = 3, isPackSplittable = true, packSize = 20m,
        });

        p.GetProperty("total").GetDecimal().Should().Be(270m);
        Arr(p.GetProperty("windows")).Select(w => w.GetProperty("allocatedQuantity").GetDecimal())
            .Should().Equal(90m, 90m, 90m);
    }

    [SkippableFact]
    public async Task The_preview_splits_an_uneven_total_highest_first_and_sums_exactly()
    {
        // The 34/33/33 the gate names. Round ONCE at the total: per-window rounding makes 100/3 into 102,
        // over-supplying the patient and over-consuming their benefit.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var p = await PreviewAsync(new
        {
            durationDays = 90, refillFrequencyCode = "Monthly",
            doseAmount = 100m / 90m, timesPerDay = 1, isPackSplittable = true, packSize = 20m,
        });

        var windows = Arr(p.GetProperty("windows"))
            .Select(w => w.GetProperty("allocatedQuantity").GetDecimal()).ToList();

        windows.Should().Equal(34m, 33m, 33m);
        windows.Sum().Should().Be(p.GetProperty("total").GetDecimal(),
            "the allocation must sum EXACTLY to the total — this is invariant 5");
    }

    [SkippableFact]
    public async Task The_preview_dates_every_window_so_the_doctor_sees_when_each_is_due()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var p = await PreviewAsync(new
        {
            durationDays = 90, refillFrequencyCode = "Monthly",
            doseAmount = 1m, timesPerDay = 1, isPackSplittable = true, packSize = 20m,
        });

        var windows = Arr(p.GetProperty("windows"));
        windows.Should().HaveCount(3);
        windows.Should().OnlyContain(w => w.GetProperty("scheduledOpen").GetString()!.Length > 0);
        windows.Should().OnlyContain(w => w.GetProperty("closesAt").GetString()!.Length > 0);
        // Window 1 gets NO early tolerance — applying it would open the window before the script existed.
        windows[0].GetProperty("opensAt").GetString()
            .Should().Be(windows[0].GetProperty("scheduledOpen").GetString());
    }

    [SkippableFact]
    public async Task The_preview_refuses_a_duration_that_is_not_chronic_with_the_same_answer_as_submit()
    {
        // A preview that accepted 14 days would show a schedule for a script the write path then refuses,
        // which is worse than no preview: the doctor is told it will work.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var r = await f.Prescriber().PostAsJsonAsync("/api/v1/prescriptions/chronic-preview", new
        {
            durationDays = 14, refillFrequencyCode = "Monthly",
            doseAmount = 1m, timesPerDay = 1, isPackSplittable = true, packSize = 20m,
        });

        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [SkippableFact]
    public async Task The_preview_reports_NotChecked_naming_the_field_rather_than_guessing_a_quantity()
    {
        // Invariant 8. The composer must be able to say WHICH fact is missing, because "could not compute"
        // on its own sends a prescriber to guess — and a silently wrong quantity is a dispensing error.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var r = await f.Prescriber().PostAsJsonAsync("/api/v1/prescriptions/chronic-preview", new
        {
            durationDays = 90, refillFrequencyCode = "Monthly",
            doseAmount = 1m, timesPerDay = 1, isPackSplittable = (bool?)null, packSize = (decimal?)null,
        });

        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("quantity-not-checked");
        // Named as the master-data COLUMN, which is the vocabulary a data administrator can act on — the
        // person who fixes this reads the drug table, not the JSON body.
        body.GetProperty("detail").GetString().Should().Contain("is_pack_splittable",
            "the MISSING FIELD is named — absence of data is never a clean result");
    }

    [SkippableFact]
    public async Task The_preview_reads_the_drugs_pack_facts_ITSELF_when_given_a_drug()
    {
        // THE DEFECT THIS PINS. The composer does not hold pack facts — they are master data, and the client
        // has no business fetching them to hand back. The preview took them from the REQUEST BODY only, so
        // the screen sent nulls, every call answered `quantity-not-checked`, and chronic prescribing was
        // unreachable for EVERY drug regardless of what the catalogue recorded.
        //
        // It resolves them the same way the submit path does, so the preview and the write agree by
        // construction rather than by the caller remembering to send the same numbers twice.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var drug = Guid.NewGuid();
        f.Packs[drug] = new DrugPack(IsPackSplittable: true, PackSize: 20m, PackContent: 20m);

        var p = await PreviewAsync(new
        {
            durationDays = 90, refillFrequencyCode = "Monthly",
            doseAmount = 1m, timesPerDay = 3, drugId = drug,
        });

        p.GetProperty("total").GetDecimal().Should().Be(270m);
        Arr(p.GetProperty("windows")).Should().HaveCount(3);
    }

    [SkippableFact]
    public async Task A_drug_whose_pack_facts_are_absent_still_reports_NotChecked_naming_the_field()
    {
        // Invariant 8 survives the change: resolving pack facts server-side must not turn "master data does
        // not record this" into a guess. 2,495 real products are in exactly this state.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var drug = Guid.NewGuid();
        f.Packs[drug] = new DrugPack(IsPackSplittable: null, PackSize: null, PackContent: null);

        var r = await f.Prescriber().PostAsJsonAsync("/api/v1/prescriptions/chronic-preview", new
        {
            durationDays = 90, refillFrequencyCode = "Monthly",
            doseAmount = 1m, timesPerDay = 1, drugId = drug,
        });

        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("title").GetString().Should().Be("quantity-not-checked");
    }

    [SkippableFact]
    public async Task The_preview_refuses_an_unknown_or_inactive_frequency()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var r = await f.Prescriber().PostAsJsonAsync("/api/v1/prescriptions/chronic-preview", new
        {
            durationDays = 90, refillFrequencyCode = "Every6Months",   // seeded INACTIVE
            doseAmount = 1m, timesPerDay = 1, isPackSplittable = true, packSize = 20m,
        });

        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
