using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 32.6 — what a chronic amendment WOULD do, before it does it.
/// </summary>
/// <remarks>
/// <para>
/// <c>AmendLineDialog</c> has had a <c>chronicPreview</c> prop since 30.3 — it renders the collected
/// quantity as immutable, the recomputed remaining windows and the new total, which is design 46 §10's
/// requirement that the doctor confirms an arithmetic they have seen. <b>No caller has ever passed it.</b>
/// The UI existed, the executor computed the same numbers on the way to writing, and nothing connected them.
/// </para>
/// <para>
/// The preview cannot be computed in the browser. <c>zChronicPreview</c> says why in its own header:
/// re-deriving largest-remainder client-side "would fork the one piece of arithmetic in this phase that must
/// not be forked — the copies would drift, and the drift would appear as a doctor being shown a schedule the
/// pharmacy never honours". So this endpoint calls <c>ChronicAmendment.Reallocate</c>, the same pure function
/// the write path calls, and writes nothing.
/// </para>
/// </remarks>
[Collection("pharmacy-db")]
public class ChronicAmendPreviewTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private sealed record Preview(
        string Outcome, decimal NewTotal, decimal AlreadyDispensed, decimal[] RemainingWindows,
        string Unit, string? MissingField);

    [SkippableFact]
    public async Task It_shows_the_remainder_split_across_the_windows_that_are_left()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            // 90 days, 3/day → 270 across three monthly windows of 90. Window 1 collected in full.
            var (rxId, lineId) = await SeedChronicAsync(app, durationDays: 90, total: 270, collectedWindow1: 90);

            var preview = await PreviewAsync(app, rxId, lineId, durationDays: 60, frequencyMonths: 1);

            preview.Outcome.Should().Be("Reallocated");
            preview.AlreadyDispensed.Should().Be(90, "what was handed over is a fact, never recalculated");
            preview.NewTotal.Should().Be(180);
            preview.RemainingWindows.Sum().Should().Be(preview.NewTotal - preview.AlreadyDispensed,
                "alreadyDispensed + Σ(remaining) == newTotal, exactly — invariant 5");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task It_refuses_a_total_below_what_was_already_collected_and_says_so()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedChronicAsync(app, durationDays: 90, total: 270, collectedWindow1: 180);

            var preview = await PreviewAsync(app, rxId, lineId, durationDays: 30, frequencyMonths: 1);

            // The doctor learns this BEFORE confirming rather than from a 409 afterwards. Un-dispensing is
            // not a thing that can happen to a patient who already has the medicine.
            preview.Outcome.Should().Be("BelowDispensed");
            preview.AlreadyDispensed.Should().Be(180);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Shortening_below_the_chronic_definition_asks_rather_than_deciding()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedChronicAsync(app, durationDays: 90, total: 270, collectedWindow1: 0);

            var asked = await PreviewAsync(app, rxId, lineId, durationDays: 20, frequencyMonths: 1);
            asked.Outcome.Should().Be("NoLongerChronic",
                "design 46 §4: not a refusal and not a silent conversion — the prescriber is asked");

            var confirmed = await PreviewAsync(app, rxId, lineId, durationDays: 20, frequencyMonths: 1,
                convertToAcute: true);
            confirmed.Outcome.Should().Be("ConvertedToAcute");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_preview_writes_nothing()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedChronicAsync(app, durationDays: 90, total: 270, collectedWindow1: 90);

            await PreviewAsync(app, rxId, lineId, durationDays: 60, frequencyMonths: 1);

            await using var db = PrescribingApiFactory.Ctx();
            var line = await db.PrescriptionLines.AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId);
            line.Status.Should().Be(RxLineStatus.PartiallyDispensed);
            line.DurationDays.Should().Be(90, "a preview is a question, not a decision");
            (await db.PrescriptionLines.CountAsync(l => l.PrescriptionId == rxId)).Should().Be(1,
                "no successor line is created by asking");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- harness

    private static async Task<Preview> PreviewAsync(
        PrescribingApiFactory app, Guid rxId, Guid lineId, int durationDays, int frequencyMonths,
        bool convertToAcute = false)
    {
        var resp = await app.Prescriber().PostAsJsonAsync(
            $"/api/v1/prescriptions/{rxId}/lines/{lineId}/amend-schedule/preview",
            new { durationDays, frequencyMonths, convertToAcute });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<Preview>(Web))!;
    }

    private static async Task<(Guid RxId, Guid LineId)> SeedChronicAsync(
        PrescribingApiFactory app, int durationDays, decimal total, decimal collectedWindow1)
    {
        await using var db = PrescribingApiFactory.Ctx();
        var rxId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var start = new DateOnly(2026, 1, 1);

        db.Prescriptions.Add(new Prescription
        {
            PrescriptionId = rxId, TenantId = Tenant,
            RxNo = "RX-2026-" + Guid.NewGuid().ToString("N")[..6],
            BeneficiaryId = app.Beneficiary, EncounterId = app.Encounter, PrescriberId = Guid.NewGuid(),
            Status = RxStatus.Approved, SubmittedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(120),
            Kind = "Chronic", RefillFrequencyCode = "Monthly", DurationDays = durationDays,
            ValidFrom = start, ValidUntil = start.AddDays(durationDays - 1),
            Lines =
            [
                new PrescriptionLine
                {
                    PrescriptionLineId = lineId, TenantId = Tenant, PrescriptionId = rxId,
                    DrugId = app.DrugA, DrugName = "Amlodipine 5mg",
                    Dose = "1 Tablet x 3/day", DoseAmount = 1, TimesPerDay = 3, Route = "PO", Frequency = "TDS",
                    QuantityPrescribed = total, QuantityDispensed = collectedWindow1,
                    DurationDays = durationDays, RootLineId = lineId,
                    Status = collectedWindow1 > 0 ? RxLineStatus.PartiallyDispensed : RxLineStatus.Active,
                },
            ],
        });
        await db.SaveChangesAsync();

        // The collected amount is spread across windows in order, never piled into the first: the database
        // refuses a window that hands over more than it allocated (ck_window_not_over_dispensed), and a
        // fixture that could not exist in production tests nothing about production.
        var perWindow = total / 3;
        var left = collectedWindow1;
        for (var i = 0; i < 3; i++)
        {
            var taken = Math.Min(left, perWindow);
            left -= taken;
            db.DispenseWindows.Add(new PrescriptionDispenseWindow
            {
                WindowId = Guid.NewGuid(), TenantId = Tenant, PrescriptionId = rxId,
                PrescriptionLineId = lineId, WindowNo = i + 1,
                ScheduledOpenDate = start.AddMonths(i),
                OpensAt = start.AddMonths(i), ClosesAt = start.AddMonths(i + 1).AddDays(-1),
                AllocatedQuantity = perWindow,
                DispensedQuantity = taken,
                Status = taken > 0 ? "Dispensed" : "Pending",
            });
        }
        await db.SaveChangesAsync();
        return (rxId, lineId);
    }
}
