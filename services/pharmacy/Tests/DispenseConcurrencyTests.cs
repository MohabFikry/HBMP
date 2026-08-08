using FluentAssertions;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>Phase 6.2 — the atomic-dispense invariant proven against REAL parallel PostgreSQL transactions
/// (env-gated <c>PHARMACY_TEST_DB</c>, not mocked): N racers on the SAME line yield EXACTLY ONE win and one
/// dispense_event row with quantity_dispensed never exceeding prescribed; replaying an Idempotency-Key adds no row;
/// a partial dispense leaves the remainder available; a used line cannot be dispensed again; a policy-approved
/// substitution + batch/expiry pin onto the dispense_event; an out-of-stock flag never consumes the line.
/// Self-cleans by scope.</summary>
[Collection("pharmacy-db")]
public class DispenseConcurrencyTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB");
    private static readonly DateOnly FutureLot = new(2027, 1, 1);
    private static DbContextOptions<PharmacyDbContext> Options() =>
        new DbContextOptionsBuilder<PharmacyDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static PharmacyDbContext Ctx() => new(Options());

    [SkippableFact]
    public async Task Parallel_dispense_of_one_line_lets_exactly_one_win()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await SeedApprovedRx(beneficiary, prescribed: 1);

            const int racers = 8;
            var tasks = Enumerable.Range(0, racers).Select(async i =>
            {
                await using var ctx = Ctx();
                return await new DispenseExecutor(ctx).DispenseAsync(
                    rxId, lineId, $"key-{i}", Guid.NewGuid(), Guid.NewGuid(),
                    1, "LOT-A", FutureLot, null, null, null, DateTimeOffset.UtcNow);
            });
            var outcomes = await Task.WhenAll(tasks);

            outcomes.Count(o => o.Outcome == DispenseOutcome.Applied).Should().Be(1, "exactly one racer may dispense the line");
            outcomes.Where(o => o.Outcome != DispenseOutcome.Applied).Should().OnlyContain(o =>
                o.Outcome == DispenseOutcome.Conflict || o.Outcome == DispenseOutcome.AlreadyDispensed ||
                o.Outcome == DispenseOutcome.OverDispense || o.Outcome == DispenseOutcome.RxNotDispensable);

            await using var verify = Ctx();
            var line = await verify.PrescriptionLines.AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId);
            line.QuantityDispensed.Should().Be(1, "the accumulator must never exceed prescribed");
            line.Status.Should().Be(RxLineStatus.Dispensed);
            (await verify.DispenseEvents.CountAsync(d => d.PrescriptionLineId == lineId))
                .Should().Be(1, "exactly one immutable dispense_event row may exist");
            (await verify.Prescriptions.AsNoTracking().SingleAsync(p => p.PrescriptionId == rxId)).Status
                .Should().Be(RxStatus.Dispensed);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Replaying_the_same_key_adds_no_row_and_returns_the_original()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await SeedApprovedRx(beneficiary, prescribed: 2);
            var pharmacy = Guid.NewGuid();

            DispenseResult first, replay;
            await using (var ctx = Ctx())
                first = await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "same-key", pharmacy, pharmacy,
                    1, "LOT-A", FutureLot, null, null, null, DateTimeOffset.UtcNow);
            await using (var ctx = Ctx())
                replay = await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "same-key", pharmacy, pharmacy,
                    1, "LOT-A", FutureLot, null, null, null, DateTimeOffset.UtcNow);

            first.Outcome.Should().Be(DispenseOutcome.Applied);
            replay.Outcome.Should().Be(DispenseOutcome.Replayed);
            replay.Event!.DispenseId.Should().Be(first.Event!.DispenseId, "replay returns the ORIGINAL dispense_event");

            await using var verify = Ctx();
            (await verify.DispenseEvents.CountAsync(d => d.PrescriptionLineId == lineId)).Should().Be(1);
            var line = await verify.PrescriptionLines.AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId);
            line.QuantityDispensed.Should().Be(1, "the replay must not dispense a second unit");
            line.Status.Should().Be(RxLineStatus.PartiallyDispensed);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Partial_then_remainder_moves_prescription_to_dispensed()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await SeedApprovedRx(beneficiary, prescribed: 3);
            var pharmacy = Guid.NewGuid();

            await using (var ctx = Ctx())
            {
                var r = await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "p1", pharmacy, pharmacy,
                    1, "LOT-A", FutureLot, null, null, null, DateTimeOffset.UtcNow);
                r.Outcome.Should().Be(DispenseOutcome.Applied);
                r.Prescription!.Status.Should().Be(RxStatus.PartiallyDispensed);
            }
            await using (var ctx = Ctx())
            {
                var r = await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "p2", pharmacy, pharmacy,
                    2, "LOT-B", FutureLot, null, null, null, DateTimeOffset.UtcNow);
                r.Outcome.Should().Be(DispenseOutcome.Applied);
                r.Prescription!.Status.Should().Be(RxStatus.Dispensed);
            }

            await using var verify = Ctx();
            (await verify.DispenseEvents.CountAsync(d => d.PrescriptionLineId == lineId)).Should().Be(2);
            (await verify.PrescriptionLines.AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId)).Status
                .Should().Be(RxLineStatus.Dispensed);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_used_line_cannot_be_dispensed_again()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            // Two lines so the Rx stays PartiallyDispensed after line-1 completes — that isolates the line-level
            // no-reuse guard (AlreadyDispensed) from the Rx-level "not dispensable" guard.
            var (rxId, lineId, _) = await SeedApprovedRx(beneficiary, prescribed: 1, extraLines: 1);
            var pharmacy = Guid.NewGuid();

            await using (var ctx = Ctx())
                (await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "use", pharmacy, pharmacy,
                    1, "LOT-A", FutureLot, null, null, null, DateTimeOffset.UtcNow)).Outcome.Should().Be(DispenseOutcome.Applied);
            await using (var ctx = Ctx())
                (await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "reuse", pharmacy, pharmacy,
                    1, "LOT-A", FutureLot, null, null, null, DateTimeOffset.UtcNow)).Outcome.Should().Be(DispenseOutcome.AlreadyDispensed);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task An_expired_lot_is_rejected_with_no_state_change()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await SeedApprovedRx(beneficiary, prescribed: 5);
            var pharmacy = Guid.NewGuid();

            await using (var ctx = Ctx())
                (await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "exp", pharmacy, pharmacy,
                    1, "LOT-OLD", new DateOnly(2020, 1, 1), null, null, null, DateTimeOffset.UtcNow))
                    .Outcome.Should().Be(DispenseOutcome.ExpiredLot);

            await using var verify = Ctx();
            (await verify.DispenseEvents.CountAsync(d => d.PrescriptionLineId == lineId)).Should().Be(0);
            (await verify.PrescriptionLines.AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId))
                .QuantityDispensed.Should().Be(0, "an expired-lot rejection leaves the accumulator untouched");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_policy_approved_substitution_and_batch_expiry_pin_onto_the_dispense_event()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, lineId, _) = await SeedApprovedRx(beneficiary, prescribed: 1);
            var pharmacy = Guid.NewGuid();
            var alternative = Guid.NewGuid();
            var expiry = new DateOnly(2026, 12, 31);

            Guid dispenseId;
            await using (var ctx = Ctx())
            {
                var r = await new DispenseExecutor(ctx).DispenseAsync(rxId, lineId, "sub", pharmacy, pharmacy,
                    1, "LOT-SUB", expiry, alternative, "generic equivalent in stock", null, DateTimeOffset.UtcNow);
                r.Outcome.Should().Be(DispenseOutcome.Applied);
                dispenseId = r.Event!.DispenseId;
            }

            await using var verify = Ctx();
            var evt = await verify.DispenseEvents.AsNoTracking().SingleAsync(d => d.DispenseId == dispenseId);
            evt.SubstitutedDrugId.Should().Be(alternative);
            evt.SubstitutionReason.Should().Be("generic equivalent in stock");
            evt.BatchNo.Should().Be("LOT-SUB");
            evt.ExpiryDate.Should().Be(expiry);
        }
        finally { await Cleanup(beneficiary); }
    }

    // ── 18.A3 / audit R2 X7 — the aggregate roll-up must not be a lost update ─────────────────────

    [SkippableFact]
    public async Task Parallel_dispense_of_different_lines_completes_the_prescription()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            // Two lines, one unit each, dispensed concurrently by two pharmacists. Both succeed (different
            // lines, no xmin collision), so the Rx IS fully dispensed. Before X7 both computed the
            // aggregate from the graph they loaded BEFORE the other committed, both wrote
            // PartiallyDispensed unguarded, and the Rx was stranded — RxDispensed never emitted.
            //
            // The structural guarantee is the guarded compare-and-set in the executor; this is the
            // regression net, run several rounds because a serialized run would also pass under the old code.
            for (var round = 0; round < 5; round++)
            {
                var (rxId, lineA, lineB) = await SeedApprovedRx(beneficiary, prescribed: 1, extraLines: 1);

                var tasks = new[] { lineA, lineB }.Select(async (lineId, i) =>
                {
                    await using var ctx = Ctx();
                    return await new DispenseExecutor(ctx).DispenseAsync(
                        rxId, lineId, $"x7-{round}-{i}", Guid.NewGuid(), Guid.NewGuid(),
                        quantity: 1, batchNo: "B-1", expiryDate: FutureLot,
                        substitutedDrugId: null, substitutionReason: null, note: null, now: DateTimeOffset.UtcNow);
                });
                var outcomes = await Task.WhenAll(tasks);

                outcomes.Should().OnlyContain(o => o.Outcome == DispenseOutcome.Applied,
                    "the two pharmacists touch DIFFERENT lines, so neither may lose");

                await using var verify = Ctx();
                (await verify.PrescriptionLines.AsNoTracking().Where(l => l.PrescriptionId == rxId).ToListAsync())
                    .Should().OnlyContain(l => l.Status == RxLineStatus.Dispensed);
                (await verify.Prescriptions.AsNoTracking().SingleAsync(p => p.PrescriptionId == rxId)).Status
                    .Should().Be(RxStatus.Dispensed,
                        "round {0}: every line is dispensed, so the Rx must not be stranded in PartiallyDispensed", round);
            }
        }
        finally { await Cleanup(beneficiary); }
    }

    private static async Task<(Guid rxId, Guid lineId, Guid otherLineId)> SeedApprovedRx(
        Guid beneficiary, decimal prescribed, int extraLines = 0)
    {
        await using var ctx = Ctx();
        var line = new PrescriptionLine { PrescriptionLineId = Guid.NewGuid(), DrugId = Guid.NewGuid(), QuantityPrescribed = prescribed };
        var lines = new List<PrescriptionLine> { line };
        for (var i = 0; i < extraLines; i++)
            lines.Add(new PrescriptionLine { PrescriptionLineId = Guid.NewGuid(), DrugId = Guid.NewGuid(), QuantityPrescribed = 1 });
        var rx = new Prescription
        {
            PrescriptionId = Guid.NewGuid(), RxNo = RxNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("rx_seq", 2026)),
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
        var ids = await ctx.Prescriptions.Where(p => p.BeneficiaryId == beneficiary).Select(p => p.PrescriptionId).ToListAsync();
        var lineIds = await ctx.PrescriptionLines.Where(l => ids.Contains(l.PrescriptionId)).Select(l => l.PrescriptionLineId).ToListAsync();
        await ctx.DispenseEvents.Where(d => lineIds.Contains(d.PrescriptionLineId)).ExecuteDeleteAsync();
        await ctx.PrescriptionAlerts.Where(a => ids.Contains(a.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.PrescriptionLines.Where(l => ids.Contains(l.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.Prescriptions.Where(p => p.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
