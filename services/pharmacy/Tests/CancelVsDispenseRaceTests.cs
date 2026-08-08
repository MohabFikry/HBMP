using FluentAssertions;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 30.2 — the medication twin of orders' <c>CancelVsConsumeRaceTests</c>, against REAL parallel PostgreSQL
/// transactions on the same harness shape as <see cref="DispenseConcurrencyTests"/>.
///
/// <para>The stake is higher here than on the investigation side. A cancelled-then-dispensed order means a
/// test was run that need not have been; a cancelled-then-dispensed prescription means a patient went home
/// with a drug their doctor had withdrawn — which is why design 46 §2 dwells on the retry: "a doctor who is
/// told 'someone else changed this' and nothing else will simply retry".</para>
/// </summary>
[Collection("pharmacy-db")]
public class CancelVsDispenseRaceTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB");
    private static PharmacyDbContext Ctx() =>
        new(new DbContextOptionsBuilder<PharmacyDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static readonly AmendReason Reason = new("DoseCorrection", "halve the dose");
    private static DateOnly GoodLot => DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));

    [SkippableFact]
    public async Task A_cancel_racing_a_dispense_produces_exactly_one_winner()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await Seed(beneficiary, prescribed: 1);

            var cancels = Enumerable.Range(0, 4).Select(async i =>
            {
                await using var ctx = Ctx();
                var r = await new AmendExecutor(ctx).CancelLineAsync(
                    rxId, lineId, $"c-{i}", Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);
                return (cancelled: r.Outcome == AmendOutcome.Applied, dispensed: false);
            });
            var dispenses = Enumerable.Range(0, 4).Select(async i =>
            {
                await using var ctx = Ctx();
                var r = await new DispenseExecutor(ctx).DispenseAsync(
                    rxId, lineId, $"d-{i}", Guid.NewGuid(), Guid.NewGuid(), 1, "B1", GoodLot,
                    null, null, null, DateTimeOffset.UtcNow);
                return (cancelled: false, dispensed: r.Outcome == DispenseOutcome.Applied);
            });

            var results = await Task.WhenAll(cancels.Concat(dispenses));
            results.Count(r => r.cancelled || r.dispensed).Should().Be(1,
                "the drug was either withdrawn or handed over — never both, and never neither");

            await using var verify = Ctx();
            var line = await verify.PrescriptionLines.AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId);
            var events = await verify.DispenseEvents.CountAsync(d => d.PrescriptionLineId == lineId);

            if (results.Any(r => r.dispensed))
            {
                line.Status.Should().Be(RxLineStatus.Dispensed);
                events.Should().Be(1);
                (await verify.LineAmendments.CountAsync(a => a.PrescriptionLineId == lineId)).Should().Be(0);
            }
            else
            {
                line.Status.Should().Be(RxLineStatus.Cancelled);
                line.QuantityDispensed.Should().Be(0);
                events.Should().Be(0, "a cancelled line must carry no dispense — that is a drug handed over "
                                    + "against a withdrawn prescription");
                (await verify.LineAmendments.CountAsync(a => a.PrescriptionLineId == lineId)).Should().Be(1);
            }
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Dispensing_a_cancelled_line_fails_with_the_reason_and_the_actor()
    {
        // THE MIRROR (design 46 §2, phase-30 Gate 2). The pharmacist needs to know the prescription was
        // withdrawn and why — "not dispensable" would send them to ring the doctor who already decided.
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await Seed(beneficiary, prescribed: 2);
            var prescriber = Guid.NewGuid();

            await using (var ctx = Ctx())
                (await new AmendExecutor(ctx).CancelLineAsync(
                    rxId, lineId, "cancel", new AmendReason("DrugUnavailable", "supplier recall"),
                    prescriber, "Dr Karim", DateTimeOffset.UtcNow)).Outcome.Should().Be(AmendOutcome.Applied);

            await using var ctx2 = Ctx();
            var dispense = await new DispenseExecutor(ctx2).DispenseAsync(
                rxId, lineId, "late", Guid.NewGuid(), Guid.NewGuid(), 1, "B1", GoodLot,
                null, null, null, DateTimeOffset.UtcNow);

            dispense.Outcome.Should().Be(DispenseOutcome.LineWithdrawn,
                "a withdrawn line is not 'already dispensed' — nothing was handed over, and the two send "
                + "the pharmacist to different places");
            dispense.Withdrawal.Should().NotBeNull();
            dispense.Withdrawal!.ReasonCode.Should().Be("DrugUnavailable");
            dispense.Withdrawal.ReasonText.Should().Be("supplier recall");
            dispense.Withdrawal.By.Should().Be(prescriber);
            dispense.Withdrawal.At.Should().NotBeNull();
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Dispensing_a_superseded_line_names_the_amendment_not_a_generic_refusal()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await Seed(beneficiary, prescribed: 30);

            await using (var ctx = Ctx())
                (await new AmendExecutor(ctx).AmendLineQuantityAsync(
                    rxId, lineId, "amend", 20, Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow))
                    .Outcome.Should().Be(AmendOutcome.Applied);

            await using var ctx2 = Ctx();
            var dispense = await new DispenseExecutor(ctx2).DispenseAsync(
                rxId, lineId, "stale", Guid.NewGuid(), Guid.NewGuid(), 30, "B1", GoodLot,
                null, null, null, DateTimeOffset.UtcNow);

            dispense.Outcome.Should().Be(DispenseOutcome.LineWithdrawn);
            dispense.Withdrawal!.ReasonCode.Should().Be("DoseCorrection");
            dispense.Withdrawal.SupersededById.Should().NotBeNull(
                "the counter must be pointed at the CORRECTED line, or the patient goes home empty-handed "
                + "with a valid prescription sitting in the system");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Amend_carries_the_dispensed_quantity_forward_and_never_rewrites_the_original()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await Seed(beneficiary, prescribed: 30);
            await using (var ctx = Ctx())
                await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "first", Guid.NewGuid(),
                    Guid.NewGuid(), 10, "B1", GoodLot, null, null, null, DateTimeOffset.UtcNow);

            AmendResult amended;
            await using (var ctx = Ctx())
                amended = await new AmendExecutor(ctx).AmendLineQuantityAsync(
                    rxId, lineId, "amend", 20, Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);

            amended.Outcome.Should().Be(AmendOutcome.Applied);

            await using var verify = Ctx();
            var original = await verify.PrescriptionLines.AsNoTracking()
                .SingleAsync(l => l.PrescriptionLineId == lineId);
            var successor = await verify.PrescriptionLines.AsNoTracking()
                .SingleAsync(l => l.PrescriptionLineId == amended.NewLineId);

            original.QuantityPrescribed.Should().Be(30, "the original says what was prescribed, for ever");
            original.Status.Should().Be(RxLineStatus.Superseded);

            successor.QuantityPrescribed.Should().Be(20);
            successor.QuantityDispensed.Should().Be(10,
                "TEN more units remain, not twenty — the dispensed portion is immutable (invariant 2)");
            successor.QuantityRemaining.Should().Be(10);
            successor.DrugId.Should().Be(original.DrugId);
            successor.Dose.Should().Be(original.Dose);
        }
        finally { await Cleanup(beneficiary); }
    }

    // ---------------------------------------------------------------- harness

    private static async Task<(Guid rxId, Guid lineId, Guid otherLineId)> Seed(
        Guid beneficiary, decimal prescribed, int extraLines = 0)
    {
        await using var ctx = Ctx();
        var line = new PrescriptionLine
        {
            PrescriptionLineId = Guid.NewGuid(), DrugId = Guid.NewGuid(), QuantityPrescribed = prescribed,
            Dose = "500mg", Route = "PO", Frequency = "BD",
        };
        var lines = new List<PrescriptionLine> { line };
        for (var i = 0; i < extraLines; i++)
            lines.Add(new PrescriptionLine
            {
                PrescriptionLineId = Guid.NewGuid(), DrugId = Guid.NewGuid(), QuantityPrescribed = 1,
            });
        var rx = new Prescription
        {
            PrescriptionId = Guid.NewGuid(),
            RxNo = RxNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("rx_seq", 2026)),
            BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), PrescriberId = Guid.NewGuid(),
            Status = RxStatus.Approved, SubmittedAt = DateTimeOffset.UtcNow, Lines = lines,
        };
        ctx.Prescriptions.Add(rx);
        await ctx.SaveChangesAsync();
        return (rx.PrescriptionId, line.PrescriptionLineId, lines[^1].PrescriptionLineId);
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        await using var ctx = Ctx();
        var ids = await ctx.Prescriptions.Where(p => p.BeneficiaryId == beneficiary)
            .Select(p => p.PrescriptionId).ToListAsync();
        var lineIds = await ctx.PrescriptionLines.Where(l => ids.Contains(l.PrescriptionId))
            .Select(l => l.PrescriptionLineId).ToListAsync();
        await ctx.LineAmendments.Where(a => ids.Contains(a.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.DispenseEvents.Where(d => lineIds.Contains(d.PrescriptionLineId)).ExecuteDeleteAsync();
        await ctx.PrescriptionAlerts.Where(a => ids.Contains(a.PrescriptionId)).ExecuteDeleteAsync();
        // ONE statement for every version: a superseded line and its successor reference each other, so
        // deleting either alone violates the other's FK.
        await ctx.PrescriptionLines.Where(l => ids.Contains(l.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.Prescriptions.Where(p => p.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
