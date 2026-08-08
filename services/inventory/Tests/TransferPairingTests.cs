using FluentAssertions;
using Mersal.Inventory.Domain;
using Mersal.Inventory.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Inventory.Tests;

/// <summary>
/// 25.5/25.6 (design 42 §5) — a branch-to-branch transfer is TWO PAIRED MOVEMENTS sharing one ref, so
/// nothing is created or destroyed in transit.
///
/// <para>The alternative — one movement that "moves" stock between branches — cannot be reconciled: each
/// clinic's ledger would have to be read together with the other's to explain its own balance, and a partial
/// failure would leave stock that had left one clinic and never arrived at the other, with no row saying so.
/// Two rows summing to zero means each branch's ledger stands alone AND the network's total is unchanged.</para>
/// </summary>
[Collection("inventory-db")]
public class TransferPairingTests
{
    [Fact]
    public void THE_PAIR_SUMS_TO_ZERO()
    {
        var (outbound, inbound) = StockRules.TransferPair(25);

        outbound.Should().Be(-25m, "stock leaves the source");
        inbound.Should().Be(+25m, "and arrives at the destination");
        (outbound + inbound).Should().Be(0m,
            "NOTHING IS CREATED OR DESTROYED IN TRANSIT — this is the invariant the pairing exists for");
    }

    [Fact]
    public void A_negative_magnitude_still_produces_a_correctly_signed_pair()
    {
        // Callers send a magnitude; the direction is the kind's business. A caller who sends -25 meaning
        // "25 out" must not invert the transfer.
        var (outbound, inbound) = StockRules.TransferPair(-25);
        outbound.Should().Be(-25m);
        inbound.Should().Be(+25m);
        (outbound + inbound).Should().Be(0m);
    }

    [Theory]
    [InlineData(MovementKind.Receipt, 1)]
    [InlineData(MovementKind.TransferIn, 1)]
    [InlineData(MovementKind.Return, 1)]
    [InlineData(MovementKind.Issue, -1)]
    [InlineData(MovementKind.TransferOut, -1)]
    [InlineData(MovementKind.WriteOff, -1)]
    public void Each_fixed_sign_kind_applies_its_own_direction(MovementKind kind, int expectedSign)
    {
        // The sign is stored on the row rather than derived at read time, so on-hand can be a plain SUM and a
        // reader never has to know the convention to get the right answer.
        StockRules.ApplySign(kind, 10).Should().Be(expectedSign * 10m);
        StockRules.ApplySign(kind, -10).Should().Be(expectedSign * 10m,
            "the caller's sign is ignored — the kind decides");
    }

    [Theory]
    [InlineData(MovementKind.Adjustment)]
    [InlineData(MovementKind.Count)]
    public void A_variance_kind_keeps_the_sign_it_was_given(MovementKind kind)
    {
        // Both record a VARIANCE and a variance goes in both directions: a stock-take that finds three MORE
        // than the books said is as real as one that finds three fewer.
        StockRules.ApplySign(kind, 3).Should().Be(3m);
        StockRules.ApplySign(kind, -3).Should().Be(-3m);
        StockRules.FixedSign(kind).Should().BeNull();
    }

    [SkippableFact]
    public async Task A_TRANSFER_LEAVES_THE_NETWORK_TOTAL_UNCHANGED()
    {
        Skip.If(StockLedgerTests.Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await StockLedgerTests.SeedItemAsync();
            var maadi = Guid.NewGuid();
            var dokki = Guid.NewGuid();
            var transferRef = Guid.NewGuid();

            await using (var db = StockLedgerTests.Ctx())
                await StockLedgerTests.Service(db).PostAsync(
                    maadi, item.ItemId, null, MovementKind.Receipt, 40, null, "u", StockLedgerTests.Key(), StockLedgerTests.Today);

            // The two halves, as the transfers endpoint writes them.
            await using (var db = StockLedgerTests.Ctx())
            {
                var svc = StockLedgerTests.Service(db);
                (await svc.PostAsync(maadi, item.ItemId, null, MovementKind.TransferOut, 15, "cover Dokki",
                    "u", StockLedgerTests.Key(), StockLedgerTests.Today, transferRef, dokki)).Outcome.Should().Be(PostOutcome.Posted);
                (await svc.PostAsync(dokki, item.ItemId, null, MovementKind.TransferIn, 15, "cover Dokki",
                    "u", StockLedgerTests.Key(), StockLedgerTests.Today, transferRef, maadi)).Outcome.Should().Be(PostOutcome.Posted);
            }

            await using (var verify = StockLedgerTests.Ctx())
            {
                var svc = StockLedgerTests.Service(verify);
                (await svc.OnHandAsync(maadi, item.ItemId, null)).Should().Be(25m);
                (await svc.OnHandAsync(dokki, item.ItemId, null)).Should().Be(15m);

                // THE INVARIANT: the pair sums to zero, so the network total is exactly what was received.
                var pair = await verify.Movements.AsNoTracking()
                    .Where(m => m.TransferRef == transferRef).ToListAsync();
                pair.Should().HaveCount(2, "one out, one in — a transfer is never a single row");
                pair.Sum(m => m.Quantity).Should().Be(0m, "nothing created or destroyed in transit");
                pair.Select(m => m.BranchId).Should().BeEquivalentTo([maadi, dokki]);
                pair.Should().OnlyContain(m => m.CounterpartyBranchId != null,
                    "each half records where the other end is, so one clinic's ledger explains itself");

                var networkTotal = await verify.Movements.AsNoTracking()
                    .Where(m => m.ItemId == item.ItemId).SumAsync(m => m.Quantity);
                networkTotal.Should().Be(40m, "the 40 received, still 40 across the network");
            }
        }
        finally { await StockLedgerTests.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_TRANSFER_CANNOT_MOVE_STOCK_A_BRANCH_DOES_NOT_HAVE()
    {
        // The outbound half is validated against the SOURCE's balance like any other reducing movement, so a
        // transfer cannot be a back door to a negative balance.
        Skip.If(StockLedgerTests.Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await StockLedgerTests.SeedItemAsync();
            var maadi = Guid.NewGuid();
            var dokki = Guid.NewGuid();

            await using (var db = StockLedgerTests.Ctx())
                await StockLedgerTests.Service(db).PostAsync(
                    maadi, item.ItemId, null, MovementKind.Receipt, 5, null, "u", StockLedgerTests.Key(), StockLedgerTests.Today);

            await using (var db = StockLedgerTests.Ctx())
            {
                var r = await StockLedgerTests.Service(db).PostAsync(
                    maadi, item.ItemId, null, MovementKind.TransferOut, 50, "too much",
                    "u", StockLedgerTests.Key(), StockLedgerTests.Today, Guid.NewGuid(), dokki);
                r.Outcome.Should().Be(PostOutcome.InsufficientStock);
            }

            await using (var verify = StockLedgerTests.Ctx())
                (await StockLedgerTests.Service(verify).OnHandAsync(maadi, item.ItemId, null)).Should().Be(5m);
        }
        finally { await StockLedgerTests.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_TRANSFER_TO_ITSELF_IS_REFUSED_BY_THE_DATABASE()
    {
        // A no-op that would still write two rows and confuse every reconciliation.
        Skip.If(StockLedgerTests.Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await StockLedgerTests.SeedItemAsync();
            var maadi = Guid.NewGuid();

            await using var db = StockLedgerTests.Ctx();
            db.Movements.Add(new StockMovement
            {
                MovementId = Guid.NewGuid(), TenantId = StockLedgerTests.Tenant, BranchId = maadi,
                ItemId = item.ItemId, Kind = MovementKind.TransferOut, Quantity = -1,
                TransferRef = Guid.NewGuid(), CounterpartyBranchId = maadi,
                Actor = "u", OccurredAt = DateTimeOffset.UtcNow, IdempotencyKey = StockLedgerTests.Key(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
        finally { await StockLedgerTests.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_NON_TRANSFER_MOVEMENT_MAY_NOT_CARRY_A_TRANSFER_REF()
    {
        // The CHECK works both ways: a transfer must have a ref, and anything else must not. A Receipt with a
        // transfer_ref would pair with nothing and make the reconciliation query lie.
        Skip.If(StockLedgerTests.Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await StockLedgerTests.SeedItemAsync();
            await using var db = StockLedgerTests.Ctx();
            db.Movements.Add(new StockMovement
            {
                MovementId = Guid.NewGuid(), TenantId = StockLedgerTests.Tenant, BranchId = Guid.NewGuid(),
                ItemId = item.ItemId, Kind = MovementKind.Receipt, Quantity = 1,
                TransferRef = Guid.NewGuid(), CounterpartyBranchId = Guid.NewGuid(),
                Actor = "u", OccurredAt = DateTimeOffset.UtcNow, IdempotencyKey = StockLedgerTests.Key(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
        finally { await StockLedgerTests.CleanupAsync(); }
    }
}
