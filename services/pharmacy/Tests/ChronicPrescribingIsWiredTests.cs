using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// The regression suite for the gap phase 30 found in phase 29: <b>the chronic machinery existed and nothing
/// ever called it.</b>
///
/// <para>Every test in <c>Mersal.Prescribing.Tests</c> passed. The allocation was right, the schema was right,
/// the sweeper ran hourly — and no endpoint ever set <c>kind='Chronic'</c>, no refill window was ever written,
/// and the counter never metered against one. A feature can be entirely correct and entirely unreachable, and
/// nothing in a green suite says so. These tests are what make "wired" checkable.</para>
/// </summary>
[Collection("pharmacy-db")]
public class ChronicPrescribingIsWiredTests(PrescribingApiFactory f) : IClassFixture<PrescribingApiFactory>
{
    [SkippableFact]
    public async Task Writing_a_chronic_script_CREATES_ITS_REFILL_WINDOWS()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var res = await SubmitAsync(Chronic(durationDays: 90, frequency: "Monthly", timesPerDay: 3));
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());

            var rxId = (await res.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("prescriptionId").GetGuid();

            await using var db = PrescribingApiFactory.Ctx();
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .SingleAsync(p => p.PrescriptionId == rxId);

            rx.Kind.Should().Be("Chronic");
            rx.RefillFrequencyCode.Should().Be("Monthly");
            rx.DurationDays.Should().Be(90);
            rx.ValidUntil.Should().Be(rx.ValidFrom!.Value.AddDays(89), "the script spans its whole duration");

            var windows = await db.DispenseWindows.AsNoTracking()
                .Where(w => w.PrescriptionId == rxId).OrderBy(w => w.WindowNo).ToListAsync();

            windows.Should().HaveCount(3, "90 days at a monthly cadence is three collections");
            windows.Sum(w => w.AllocatedQuantity).Should().Be(rx.Lines.Single().QuantityPrescribed,
                "the windows sum EXACTLY to the prescribed total — round once, at the total");
            windows.Should().OnlyContain(w => w.Status == "Pending");

            // Window 1 opens on the day it is scheduled; the rest carry the early tolerance.
            windows[0].OpensAt.Should().Be(windows[0].ScheduledOpenDate,
                "applying the tolerance to window 1 would put opens_at before the script existed");
            windows[1].OpensAt.Should().Be(windows[1].ScheduledOpenDate.AddDays(-5));
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_ACUTE_script_is_unchanged_and_carries_no_schedule()
    {
        // The property that makes this safe to add to the path every prescription goes through.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var res = await SubmitAsync(Acute());
            res.StatusCode.Should().Be(HttpStatusCode.Created);
            var rxId = (await res.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("prescriptionId").GetGuid();

            await using var db = PrescribingApiFactory.Ctx();
            (await db.Prescriptions.AsNoTracking().SingleAsync(p => p.PrescriptionId == rxId)).Kind
                .Should().Be("Acute");
            (await db.DispenseWindows.AsNoTracking().CountAsync(w => w.PrescriptionId == rxId))
                .Should().Be(0, "an acute script carries no refill schedule");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_chronic_script_of_one_month_or_less_is_refused_with_the_definition()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var res = await SubmitAsync(Chronic(durationDays: 30, frequency: "Monthly", timesPerDay: 1));
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()
                .Should().Contain("not chronic", "a 14-day course is not chronic and the refusal says so");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_acute_script_may_not_smuggle_in_a_refill_frequency()
    {
        // "Allowing one would make 'is this chronic?' answerable two ways."
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var body = Acute();
            var res = await SubmitAsync(body with { RefillFrequencyCode = "Monthly" });
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_unknown_or_inactive_refill_frequency_is_refused_rather_than_producing_no_windows()
    {
        // A chronic script with no windows is undispensable in a way nothing reports — the failure the
        // migration's CHECK guards against, refused here before anything is written.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var res = await SubmitAsync(Chronic(90, "Every6Months", 1));   // seeded INACTIVE by 0012
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

            await using var db = PrescribingApiFactory.Ctx();
            (await db.Prescriptions.AsNoTracking().CountAsync(p => p.BeneficiaryId == f.Beneficiary))
                .Should().Be(0, "a refusal writes nothing at all");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_COUNTER_refuses_a_collection_outside_its_window_and_names_the_date()
    {
        // The second half of the wiring. Without it the windows were decoration: rows the sweeper forfeited
        // and nothing ever enforced, so a three-month script was collectable in full on day one.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var rxId = (await (await SubmitAsync(Chronic(90, "Monthly", 3)))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("prescriptionId").GetGuid();

            await using var db = PrescribingApiFactory.Ctx();
            var line = await db.PrescriptionLines.AsNoTracking().SingleAsync(l => l.PrescriptionId == rxId);
            var windows = await db.DispenseWindows.AsNoTracking()
                .Where(w => w.PrescriptionLineId == line.PrescriptionLineId)
                .OrderBy(w => w.WindowNo).ToListAsync();

            await using var ctx = PrescribingApiFactory.Ctx();
            var executor = new DispenseExecutor(ctx);
            // Unique per run: the idempotency key is UNIQUE across the whole table, so a fixed literal
            // collides with a previous run's row and reports IdempotencyKeyReuse.
            var run = Guid.NewGuid().ToString("N")[..8];
            var lot = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));

            // Inside window 1: allowed, and it moves the WINDOW's accumulator, not only the line's.
            var first = await executor.DispenseAsync(rxId, line.PrescriptionLineId, $"w1-{run}", Guid.NewGuid(),
                Guid.NewGuid(), windows[0].AllocatedQuantity, "B1", lot, null, null, null,
                new DateTimeOffset(windows[0].ScheduledOpenDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
            first.Outcome.Should().Be(DispenseOutcome.Applied);

            await using var verify = PrescribingApiFactory.Ctx();
            var w1 = await verify.DispenseWindows.AsNoTracking().SingleAsync(w => w.WindowId == windows[0].WindowId);
            w1.DispensedQuantity.Should().Be(windows[0].AllocatedQuantity,
                "the window's own accumulator must move, or the schedule never records what was collected");
            w1.Status.Should().Be("Dispensed");

            // The SAME day again: window 1 is spent and window 2 has not opened. The refusal names when.
            await using var ctx2 = PrescribingApiFactory.Ctx();
            var second = await new DispenseExecutor(ctx2).DispenseAsync(rxId, line.PrescriptionLineId, $"w1-again-{run}",
                Guid.NewGuid(), Guid.NewGuid(), 1, "B1", lot, null, null, null,
                new DateTimeOffset(windows[0].ScheduledOpenDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

            second.Outcome.Should().Be(DispenseOutcome.OutsideRefillWindow,
                "a three-month script is not collectable in full on day one");
            second.Refill!.OpensAt.Should().Be(windows[1].OpensAt,
                "the pharmacist has the beneficiary in front of them and must be able to say when to return");
        }
        finally { await f.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- harness

    private sealed record Body(
        Guid BeneficiaryId, Guid EncounterId, DateTimeOffset? ExpiresAt, bool AcknowledgeAlerts,
        object[] Lines, string? Kind = null, string? RefillFrequencyCode = null, int? DurationDays = null);

    private Body Acute() => new(
        f.Beneficiary, Guid.NewGuid(), null, true,
        [new { drugId = Guid.NewGuid(), dose = "500mg", route = "PO", frequency = "TDS",
               quantityPrescribed = 21m, refillsAllowed = 0, durationDays = 7 }]);

    private Body Chronic(int durationDays, string frequency, int timesPerDay) => new(
        f.Beneficiary, Guid.NewGuid(), null, true,
        [new { drugId = Guid.NewGuid(), dose = "5mg", route = "PO", frequency = "TDS",
               quantityPrescribed = 1m, refillsAllowed = 0, durationDays,
               doseAmount = 1m, doseUnit = "tablet", timesPerDay }],
        Kind: "Chronic", RefillFrequencyCode: frequency, DurationDays: durationDays);

    private async Task<HttpResponseMessage> SubmitAsync(Body body)
    {
        var doctor = f.Prescriber();
        doctor.DefaultRequestHeaders.Add("Idempotency-Key", $"rx-{Guid.NewGuid()}");
        return await doctor.PostAsJsonAsync("/api/v1/prescriptions", body);
    }
}
