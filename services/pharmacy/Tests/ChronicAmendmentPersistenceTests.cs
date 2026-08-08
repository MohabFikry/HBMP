using FluentAssertions;
using Mersal.Amendment;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 30.3 — what a chronic amendment does to the SCHEDULE (design 46 §4, and the decision recorded in
/// docs/superpowers/specs/2026-08-07-chronic-amendment-design.md).
///
/// <para>The arithmetic is proven pure in <c>Mersal.Prescribing.Tests.ChronicAmendmentTests</c>. What is
/// proven here is the part that touches rows, and the property it must hold: <b>nothing is moved and nothing
/// is copied.</b> The original line keeps its whole schedule with the collected windows exactly as they were;
/// its uncollected windows step aside into a terminal status the sweeper cannot see; the successor gets a
/// fresh schedule.</para>
/// </summary>
[Collection("pharmacy-db")]
public class ChronicAmendmentPersistenceTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB");
    private static PharmacyDbContext Ctx() =>
        new(new DbContextOptionsBuilder<PharmacyDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static readonly AmendReason Reason = new("ClinicalChange", "blood pressure settled");
    private static readonly DateOnly Start = new(2026, 1, 1);

    [SkippableFact]
    public async Task Shortening_a_started_script_supersedes_the_line_and_leaves_the_collected_window_untouched()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            // 90 days monthly, 3/day → 270 over three windows of 90. Window 1 collected in full.
            var (rxId, lineId) = await SeedChronic(beneficiary, durationDays: 90, total: 270, collectedWindow1: 90);

            ChronicAmendResult result;
            await using (var ctx = Ctx())
                result = await new ChronicAmendExecutor(ctx).AmendScheduleAsync(
                    rxId, lineId, "amend-1", new ChronicAmendRequest(60, 1), Reason,
                    Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow, Start, toleranceDays: 5);

            result.Outcome.Should().Be(AmendOutcome.Applied);
            result.Reallocation!.NewTotal.Should().Be(180);
            result.Reallocation.RemainingWindows.Should().Equal(90);

            await using var verify = Ctx();
            var original = await verify.PrescriptionLines.AsNoTracking()
                .SingleAsync(l => l.PrescriptionLineId == lineId);
            var successor = await verify.PrescriptionLines.AsNoTracking()
                .SingleAsync(l => l.PrescriptionLineId == result.NewLineId);

            original.Status.Should().Be(RxLineStatus.Superseded);
            original.QuantityPrescribed.Should().Be(270, "the original is never rewritten");
            original.DurationDays.Should().Be(90);

            successor.QuantityPrescribed.Should().Be(180);
            successor.QuantityDispensed.Should().Be(90, "the dispensed portion carries forward");
            successor.DurationDays.Should().Be(60);
            successor.RootLineId.Should().Be(original.RootLineId);

            // ---- The schedule: nothing moved, nothing copied ----
            var oldWindows = await verify.DispenseWindows.AsNoTracking()
                .Where(w => w.PrescriptionLineId == lineId).OrderBy(w => w.WindowNo).ToListAsync();
            oldWindows.Should().HaveCount(3, "the original keeps its WHOLE schedule");

            oldWindows[0].DispensedQuantity.Should().Be(90);
            oldWindows[0].Status.Should().Be("Dispensed", "a collected window is a fact and is untouched");
            oldWindows[0].SupersededByAmendmentId.Should().BeNull();

            oldWindows.Skip(1).Should().OnlyContain(w => w.Status == "Superseded",
                "an uncollected window on a replaced line must leave the sweeper's sight, or it records a "
                + "forfeiture for a collection that was never owed");
            oldWindows.Skip(1).Should().OnlyContain(w => w.SupersededByAmendmentId == result.AmendmentId);

            var newWindows = await verify.DispenseWindows.AsNoTracking()
                .Where(w => w.PrescriptionLineId == successor.PrescriptionLineId)
                .OrderBy(w => w.WindowNo).ToListAsync();
            newWindows.Should().ContainSingle();
            newWindows[0].AllocatedQuantity.Should().Be(90);
            newWindows[0].WindowNo.Should().Be(1, "the successor's schedule is its own, numbered from 1");

            // THE ANCHOR: the day after the collected window closes. Not today — a patient who collected on
            // the 1st must not be able to collect again on the 3rd because their script was amended.
            newWindows[0].ScheduledOpenDate.Should().Be(oldWindows[0].ClosesAt.AddDays(1));
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Extending_re_allocates_the_remainder_across_the_new_windows_and_still_sums()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId) = await SeedChronic(beneficiary, 90, 270, collectedWindow1: 90);

            ChronicAmendResult result;
            await using (var ctx = Ctx())
                result = await new ChronicAmendExecutor(ctx).AmendScheduleAsync(
                    rxId, lineId, "amend-2", new ChronicAmendRequest(120, 1), Reason,
                    Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow, Start, toleranceDays: 5);

            result.Outcome.Should().Be(AmendOutcome.Applied);
            result.Reallocation!.NewTotal.Should().Be(360);
            result.Reallocation.RemainingWindows.Should().Equal(90, 90, 90);

            await using var verify = Ctx();
            var newWindows = await verify.DispenseWindows.AsNoTracking()
                .Where(w => w.PrescriptionLineId == result.NewLineId).ToListAsync();
            newWindows.Should().HaveCount(3);
            (90m + newWindows.Sum(w => w.AllocatedQuantity)).Should().Be(360,
                "dispensed + Σ(new windows) == the new total, exactly (invariant 4)");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_total_below_what_was_collected_is_refused_and_writes_nothing()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId) = await SeedChronic(beneficiary, 90, 270, collectedWindow1: 90);

            ChronicAmendResult result;
            await using (var ctx = Ctx())
                result = await new ChronicAmendExecutor(ctx).AmendScheduleAsync(
                    rxId, lineId, "too-low", new ChronicAmendRequest(20, 1), Reason,
                    Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow, Start, toleranceDays: 5);

            result.Outcome.Should().Be(AmendOutcome.BelowDispensed);

            await using var verify = Ctx();
            (await verify.PrescriptionLines.AsNoTracking().CountAsync(l => l.PrescriptionId == rxId))
                .Should().Be(1, "a refused amendment creates no successor");
            (await verify.PrescriptionLines.AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId))
                .Status.Should().Be(RxLineStatus.PartiallyDispensed, "and does not touch the original");
            (await verify.DispenseWindows.AsNoTracking()
                .CountAsync(w => w.PrescriptionLineId == lineId && w.Status == "Superseded"))
                .Should().Be(0, "nor its schedule");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Reducing_below_the_chronic_definition_is_reported_rather_than_decided()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId) = await SeedChronic(beneficiary, 90, 270, collectedWindow1: 0);

            await using (var ctx = Ctx())
            {
                var refused = await new ChronicAmendExecutor(ctx).AmendScheduleAsync(
                    rxId, lineId, "to-25", new ChronicAmendRequest(25, 1), Reason,
                    Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow, Start, toleranceDays: 5);
                refused.Outcome.Should().Be(AmendOutcome.NoChange);
                refused.Reallocation!.Outcome.Should().Be(Prescribing.AmendmentOutcome.NoLongerChronic);
            }

            await using var verify0 = Ctx();
            (await verify0.PrescriptionLines.AsNoTracking().CountAsync(l => l.PrescriptionId == rxId))
                .Should().Be(1, "without the prescriber's confirmation, nothing happens");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Converting_to_acute_on_confirmation_drops_the_schedule_and_records_it()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId) = await SeedChronic(beneficiary, 90, 270, collectedWindow1: 0);

            ChronicAmendResult result;
            await using (var ctx = Ctx())
                result = await new ChronicAmendExecutor(ctx).AmendScheduleAsync(
                    rxId, lineId, "to-acute", new ChronicAmendRequest(25, 1, ConvertToAcute: true), Reason,
                    Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow, Start, toleranceDays: 5);

            result.Outcome.Should().Be(AmendOutcome.Applied);

            await using var verify = Ctx();
            var rx = await verify.Prescriptions.AsNoTracking().SingleAsync(p => p.PrescriptionId == rxId);
            rx.Kind.Should().Be("Acute", "the system must not keep a 'chronic' script that is not chronic");
            rx.RefillFrequencyCode.Should().BeNull("an acute script carries no refill schedule");

            (await verify.DispenseWindows.AsNoTracking().CountAsync(w => w.PrescriptionLineId == result.NewLineId))
                .Should().Be(0);

            var record = await verify.LineAmendments.AsNoTracking()
                .SingleAsync(a => a.AmendmentId == result.AmendmentId);
            record.ReasonText.Should().Contain("converted to acute",
                "the conversion changes the dispensing pattern the patient was told to expect, so it is "
                + "recorded rather than merely applied");
        }
        finally { await Cleanup(beneficiary); }
    }

    // ---------------------------------------------------------------- harness

    private static async Task<(Guid rxId, Guid lineId)> SeedChronic(
        Guid beneficiary, int durationDays, decimal total, decimal collectedWindow1)
    {
        await using var ctx = Ctx();
        var line = new PrescriptionLine
        {
            PrescriptionLineId = Guid.NewGuid(), DrugId = Guid.NewGuid(), DrugName = "Amlodipine 5mg",
            Dose = "5mg", Route = "PO", Frequency = "TDS",
            QuantityPrescribed = total, QuantityDispensed = collectedWindow1, DurationDays = durationDays,
            Status = collectedWindow1 > 0 ? RxLineStatus.PartiallyDispensed : RxLineStatus.Active,
        };
        var rx = new Prescription
        {
            PrescriptionId = Guid.NewGuid(),
            RxNo = RxNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("rx_seq", 2026)),
            BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), PrescriberId = Guid.NewGuid(),
            Status = RxStatus.Approved, SubmittedAt = DateTimeOffset.UtcNow,
            Kind = "Chronic", RefillFrequencyCode = "Monthly", DurationDays = durationDays,
            ValidFrom = Start, ValidUntil = Start.AddDays(durationDays - 1),
            Lines = [line],
        };
        ctx.Prescriptions.Add(rx);
        await ctx.SaveChangesAsync();

        // Three monthly windows of 90; window 1 collected iff asked for.
        for (var i = 0; i < 3; i++)
            ctx.DispenseWindows.Add(new PrescriptionDispenseWindow
            {
                WindowId = Guid.NewGuid(), TenantId = rx.TenantId, PrescriptionId = rx.PrescriptionId,
                PrescriptionLineId = line.PrescriptionLineId, WindowNo = i + 1,
                ScheduledOpenDate = Start.AddDays(i * 30),
                OpensAt = i == 0 ? Start : Start.AddDays(i * 30 - 5),
                ClosesAt = Start.AddDays((i + 1) * 30 - 1),
                AllocatedQuantity = 90,
                DispensedQuantity = i == 0 ? collectedWindow1 : 0,
                Status = i == 0 && collectedWindow1 > 0 ? "Dispensed" : "Pending",
            });
        await ctx.SaveChangesAsync();
        return (rx.PrescriptionId, line.PrescriptionLineId);
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        await using var ctx = Ctx();
        var ids = await ctx.Prescriptions.Where(p => p.BeneficiaryId == beneficiary)
            .Select(p => p.PrescriptionId).ToListAsync();
        await ctx.DispenseWindows.Where(w => ids.Contains(w.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.LineAmendments.Where(a => ids.Contains(a.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.PrescriptionLines.Where(l => ids.Contains(l.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.Prescriptions.Where(p => p.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
