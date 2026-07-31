using FluentAssertions;
using Mersal.Data;
using Mersal.Inventory.Domain;
using Mersal.Inventory.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Inventory.Tests;

/// <summary>
/// 25.5 (design 42 §5/§7 rule 7) — on-hand is derived from an APPEND-ONLY ledger, and every rule that
/// protects it holds against a real database.
///
/// <para>Two of these are the ones that matter most and they are named for it: the ledger cannot be edited,
/// and two parallel issues of the last unit produce exactly one success. Everything else is a rule; those
/// two are the reason the design chose a ledger at all.</para>
/// </summary>
[Collection("inventory-db")]
public class StockLedgerTests
{
    internal static readonly string? Owner = Environment.GetEnvironmentVariable("INVENTORY_TEST_DB");

    internal const string Tenant = "t-inventory-tests";

    internal static InventoryDbContext Ctx()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);
        return db;
    }

    internal static MovementService Service(InventoryDbContext db, DateTimeOffset? at = null) =>
        new(db, new RlsContext { TenantId = Tenant }, new FixedClock(at ?? new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero)));

    internal static readonly DateOnly Today = new(2026, 8, 1);

    internal static async Task<Item> SeedItemAsync(ItemCategory category = ItemCategory.NonMedical)
    {
        await using var db = Ctx();
        var now = DateTimeOffset.UtcNow;
        var medical = category == ItemCategory.Medical;
        var item = new Item
        {
            ItemId = Guid.NewGuid(), TenantId = Tenant, Sku = "SKU-" + Guid.NewGuid().ToString("N")[..8],
            NameEn = "Test item", NameAr = "صنف اختبار", Category = category, UnitOfMeasure = "each",
            IsBatchTracked = medical, RequiresExpiry = medical, IsControlled = false,
            Status = ItemStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    internal static async Task<StockBatch> SeedBatchAsync(Guid itemId, DateOnly? expiry)
    {
        await using var db = Ctx();
        var batch = new StockBatch
        {
            BatchId = Guid.NewGuid(), TenantId = Tenant, ItemId = itemId,
            BatchNo = "B-" + Guid.NewGuid().ToString("N")[..6], ExpiryDate = expiry,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        return batch;
    }

    internal static async Task CleanupAsync()
    {
        await using var db = Ctx();
        // The ledger REFUSES DELETE by trigger, so the test cleanup has to disable it — which is itself the
        // clearest statement of what the trigger does.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE inventory.stock_movement DISABLE TRIGGER trg_stock_movement_no_mutate;
            DELETE FROM inventory.stock_movement WHERE tenant_id = {0};
            ALTER TABLE inventory.stock_movement ENABLE TRIGGER trg_stock_movement_no_mutate;
            DELETE FROM inventory.stock_batch WHERE tenant_id = {0};
            DELETE FROM inventory.branch_item WHERE tenant_id = {0};
            DELETE FROM inventory.item_history WHERE tenant_id = {0};
            DELETE FROM inventory.item WHERE tenant_id = {0};
            """, Tenant);
    }

    // ---- the two that matter most ------------------------------------------------------------------------

    [SkippableFact]
    public async Task THE_LEDGER_CANNOT_BE_EDITED_update_and_delete_are_both_refused()
    {
        // A ledger that can be edited is a balance nobody can reconcile — the whole reason on-hand is derived
        // rather than stored. Enforced at the DATABASE, so a repair script and a psql session are refused too.
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync();
            var branch = Guid.NewGuid();

            await using (var db = Ctx())
                (await Service(db).PostAsync(branch, item.ItemId, null, MovementKind.Receipt, 10, null, "u1", Key(), Today))
                    .Outcome.Should().Be(PostOutcome.Posted);

            await using (var db = Ctx())
            {
                var update = async () => await db.Database.ExecuteSqlRawAsync(
                    "UPDATE inventory.stock_movement SET quantity = 999 WHERE tenant_id = {0}", Tenant);
                await update.Should().ThrowAsync<Exception>("the ledger is append-only: UPDATE is denied");

                var delete = async () => await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM inventory.stock_movement WHERE tenant_id = {0}", Tenant);
                await delete.Should().ThrowAsync<Exception>("the ledger is append-only: DELETE is denied");
            }

            // And the balance is untouched by the attempts.
            await using (var db = Ctx())
                (await Service(db).OnHandAsync(branch, item.ItemId, null)).Should().Be(10m);
        }
        finally { await CleanupAsync(); }
    }

    [SkippableFact]
    public async Task PARALLEL_ISSUE_OF_THE_LAST_UNIT_YIELDS_EXACTLY_ONE_SUCCESS()
    {
        // The concurrency proof. Reading the balance and then inserting — the obvious shape — is precisely the
        // interleaving that drives stock negative, and the failure is invisible until someone reconciles.
        // MovementService takes a row lock inside the transaction that writes, so one of these waits.
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync();
            var branch = Guid.NewGuid();

            await using (var db = Ctx())
                await Service(db).PostAsync(branch, item.ItemId, null, MovementKind.Receipt, 1, null, "u1", Key(), Today);

            // Two callers, ONE unit, distinct idempotency keys — so this is a genuine race, not a replay.
            var results = await Task.WhenAll(
                IssueOneAsync(branch, item.ItemId, Key()),
                IssueOneAsync(branch, item.ItemId, Key()));

            results.Count(r => r == PostOutcome.Posted).Should().Be(1, "exactly one wins");
            results.Count(r => r == PostOutcome.InsufficientStock).Should().Be(1, "and exactly one is refused");

            await using (var db = Ctx())
                (await Service(db).OnHandAsync(branch, item.ItemId, null)).Should().Be(0m,
                    "ZERO, never -1 — negative on-hand is impossible");
        }
        finally { await CleanupAsync(); }

        static async Task<PostOutcome> IssueOneAsync(Guid branch, Guid itemId, string key)
        {
            await using var db = Ctx();
            var r = await Service(db).PostAsync(branch, itemId, null, MovementKind.Issue, 1, null, "u", key, Today);
            return r.Outcome;
        }
    }

    // ---- on-hand is the ledger sum -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ON_HAND_ALWAYS_EQUALS_THE_LEDGER_SUM()
    {
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync();
            var branch = Guid.NewGuid();

            await using (var db = Ctx())
            {
                var svc = Service(db);
                await svc.PostAsync(branch, item.ItemId, null, MovementKind.Receipt, 100, null, "u", Key(), Today);
                await svc.PostAsync(branch, item.ItemId, null, MovementKind.Issue, 30, null, "u", Key(), Today);
                await svc.PostAsync(branch, item.ItemId, null, MovementKind.Return, 5, null, "u", Key(), Today);
                await svc.PostAsync(branch, item.ItemId, null, MovementKind.Adjustment, -4, "recount", "u", Key(), Today);
                await svc.PostAsync(branch, item.ItemId, null, MovementKind.WriteOff, 6, "damaged in store", "u", Key(), Today);
            }

            await using (var db = Ctx())
            {
                // 100 − 30 + 5 − 4 − 6 = 65, and it is a SUM, recomputed, not a column anyone maintained.
                (await Service(db).OnHandAsync(branch, item.ItemId, null)).Should().Be(65m);

                var raw = await db.Movements.AsNoTracking()
                    .Where(m => m.BranchId == branch).SumAsync(m => m.Quantity);
                raw.Should().Be(65m, "the derived balance and the raw sum are the same thing by construction");
            }
        }
        finally { await CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_STOCK_TAKE_records_a_VARIANCE_and_never_overwrites_history()
    {
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync();
            var branch = Guid.NewGuid();

            await using (var db = Ctx())
            {
                var svc = Service(db);
                await svc.PostAsync(branch, item.ItemId, null, MovementKind.Receipt, 50, null, "u", Key(), Today);
                // The shelf says 47. The variance is −3, recorded as a Count.
                await svc.PostAsync(branch, item.ItemId, null, MovementKind.Count, -3, "annual stock-take", "u", Key(), Today);
            }

            await using (var db = Ctx())
            {
                (await Service(db).OnHandAsync(branch, item.ItemId, null)).Should().Be(47m);
                var rows = await db.Movements.AsNoTracking().Where(m => m.BranchId == branch).ToListAsync();
                rows.Should().HaveCount(2, "the receipt SURVIVES — a stock-take adds a row, it does not rewrite one");
                rows.Should().Contain(m => m.Kind == MovementKind.Receipt && m.Quantity == 50m);
            }
        }
        finally { await CleanupAsync(); }
    }

    // ---- idempotency -------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_REPLAYED_MOVEMENT_APPLIES_ONCE()
    {
        // A double-posted receipt is a phantom stock level, and the ledger has no UPDATE to correct it with.
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync();
            var branch = Guid.NewGuid();
            var key = Key();

            await using (var db = Ctx())
            {
                var svc = Service(db);
                var first = await svc.PostAsync(branch, item.ItemId, null, MovementKind.Receipt, 20, null, "u", key, Today);
                var second = await svc.PostAsync(branch, item.ItemId, null, MovementKind.Receipt, 20, null, "u", key, Today);

                first.Outcome.Should().Be(PostOutcome.Posted);
                second.Outcome.Should().Be(PostOutcome.Replayed);
                second.Movement!.MovementId.Should().Be(first.Movement!.MovementId, "the ORIGINAL is returned");
            }

            await using (var db = Ctx())
            {
                (await Service(db).OnHandAsync(branch, item.ItemId, null)).Should().Be(20m, "once, not forty");
                (await db.Movements.AsNoTracking().CountAsync(m => m.BranchId == branch)).Should().Be(1);
            }
        }
        finally { await CleanupAsync(); }
    }

    // ---- expiry quarantine -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task AN_EXPIRED_BATCH_CANNOT_BE_ISSUED()
    {
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync(ItemCategory.Medical);
            var expiry = new DateOnly(2026, 7, 1);
            var batch = await SeedBatchAsync(item.ItemId, expiry);
            var branch = Guid.NewGuid();

            // RECEIVED WHILE STILL VALID — which is the only way stock legitimately gets on a shelf, and the
            // only setup under which this test proves anything. Receiving already-expired stock is refused by
            // the same rule, so doing it that way would leave on-hand at zero and the issue would fail for
            // insufficient stock while appearing to prove the expiry gate.
            await using (var db = Ctx())
                (await Service(db).PostAsync(branch, item.ItemId, batch.BatchId, MovementKind.Receipt, 10, null, "u", Key(),
                    today: expiry.AddDays(-30))).Outcome.Should().Be(PostOutcome.Posted);

            // A MONTH LATER the batch has lapsed on the shelf, and the issue is refused.
            await using (var db = Ctx())
            {
                var issue = await Service(db).PostAsync(branch, item.ItemId, batch.BatchId, MovementKind.Issue, 1, null, "u", Key(), Today);
                issue.Outcome.Should().Be(PostOutcome.BatchExpired);
            }

            // AND THE NEGATION: the same issue a day before expiry succeeds, so the refusal above is the
            // expiry rule and not some other refusal wearing its name.
            await using (var db = Ctx())
                (await Service(db).PostAsync(branch, item.ItemId, batch.BatchId, MovementKind.Issue, 1, null, "u", Key(),
                    today: expiry.AddDays(-1))).Outcome.Should().Be(PostOutcome.Posted);
        }
        finally { await CleanupAsync(); }
    }

    [SkippableFact]
    public async Task BUT_AN_EXPIRED_BATCH_CAN_BE_WRITTEN_OFF_WITH_A_REASON()
    {
        // The exemption IS the quarantine mechanism. If expiry blocked every movement, expired stock could
        // never leave the ledger and would sit on the balance for ever.
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync(ItemCategory.Medical);
            var expiry = new DateOnly(2026, 7, 1);
            var batch = await SeedBatchAsync(item.ItemId, expiry);
            var branch = Guid.NewGuid();

            // Received while valid; it lapses on the shelf. See the sibling test for why the date matters.
            await using (var db = Ctx())
                (await Service(db).PostAsync(branch, item.ItemId, batch.BatchId, MovementKind.Receipt, 10, null, "u", Key(),
                    today: expiry.AddDays(-30))).Outcome.Should().Be(PostOutcome.Posted);

            await using (var db = Ctx())
            {
                var noReason = await Service(db).PostAsync(branch, item.ItemId, batch.BatchId, MovementKind.WriteOff, 10, null, "u", Key(), Today);
                noReason.Outcome.Should().Be(PostOutcome.ReasonRequired, "clearing quarantined stock must say why");

                var withReason = await Service(db).PostAsync(branch, item.ItemId, batch.BatchId, MovementKind.WriteOff, 10, "expired 2026-07-01", "u", Key(), Today);
                withReason.Outcome.Should().Be(PostOutcome.Posted);
            }

            await using (var db = Ctx())
                (await Service(db).OnHandAsync(branch, item.ItemId, batch.BatchId)).Should().Be(0m);
        }
        finally { await CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_BATCH_TRACKED_ITEM_REFUSES_A_MOVEMENT_WITH_NO_BATCH()
    {
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync(ItemCategory.Medical);
            await using var db = Ctx();
            var r = await Service(db).PostAsync(Guid.NewGuid(), item.ItemId, null, MovementKind.Receipt, 5, null, "u", Key(), Today);
            r.Outcome.Should().Be(PostOutcome.BatchRequired);
        }
        finally { await CleanupAsync(); }
    }

    // ---- schema-level rules ------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_MEDICAL_ITEM_CANNOT_BE_CREATED_WITHOUT_BATCH_AND_EXPIRY_TRACKING()
    {
        // A medical consumable whose batch nobody recorded cannot be recalled, and one whose expiry nobody
        // recorded cannot be blocked from issue. Enforced by CHECK, not by the endpoint alone.
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            await using var db = Ctx();
            db.Items.Add(new Item
            {
                ItemId = Guid.NewGuid(), TenantId = Tenant, Sku = "SKU-" + Guid.NewGuid().ToString("N")[..8],
                NameEn = "Bad", NameAr = "سيئ", Category = ItemCategory.Medical, UnitOfMeasure = "each",
                IsBatchTracked = false, RequiresExpiry = false,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
        finally { await CleanupAsync(); }
    }

    [SkippableFact]
    public async Task CONTROLLED_SUBSTANCES_ARE_BLOCKED_BY_CONSTRAINT_NOT_BY_CONVENTION()
    {
        // D1. Enabling them must be a deliberate, reviewable MIGRATION — not a checkbox someone ticks at 4pm.
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            await using var db = Ctx();
            db.Items.Add(new Item
            {
                ItemId = Guid.NewGuid(), TenantId = Tenant, Sku = "SKU-" + Guid.NewGuid().ToString("N")[..8],
                NameEn = "Morphine", NameAr = "مورفين", Category = ItemCategory.Medical, UnitOfMeasure = "ampoule",
                IsBatchTracked = true, RequiresExpiry = true, IsControlled = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "ck_item_no_controlled_substances pins it to false (ADR-0029 D1)");
        }
        finally { await CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_MEDICAL_BATCH_MUST_CARRY_AN_EXPIRY_DATE()
    {
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        try
        {
            var item = await SeedItemAsync(ItemCategory.Medical);
            await using var db = Ctx();
            db.Batches.Add(new StockBatch
            {
                BatchId = Guid.NewGuid(), TenantId = Tenant, ItemId = item.ItemId,
                BatchNo = "B-1", ExpiryDate = null, CreatedAt = DateTimeOffset.UtcNow,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<Exception>("the trigger requires an expiry for a tracked item");
        }
        finally { await CleanupAsync(); }
    }

    [SkippableFact]
    public async Task THERE_IS_NO_QUANTITY_ON_HAND_COLUMN_ANYWHERE_IN_THE_SCHEMA()
    {
        // Design 42 §7 rule 7, asserted against the live catalog rather than against the migration text: a
        // column added by a later migration would pass a text scan of 0001 and fail here.
        Skip.If(Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        await using var db = Ctx();
        var offenders = await db.Database.SqlQuery<string>($"""
            SELECT table_name || '.' || column_name AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'inventory'
              AND (column_name LIKE '%on_hand%' OR column_name LIKE '%quantity_on%' OR column_name = 'balance')
              AND table_name <> 'stock_on_hand'
            """).ToListAsync();

        offenders.Should().BeEmpty(
            "on-hand is DERIVED from the ledger (design 42 §7 rule 7). A stored balance is one that drifts, " +
            "and a balance you cannot reconcile is a number people stop trusting. Offenders: {0}",
            string.Join(", ", offenders));
    }

    internal static string Key() => "idem-" + Guid.NewGuid().ToString("N");
}
