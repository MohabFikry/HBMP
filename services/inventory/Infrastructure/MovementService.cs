using System.Globalization;
using Mersal.Data;
using Mersal.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Inventory.Infrastructure;

/// <summary>The outcome of posting a movement, in the shape the endpoint maps to a status code.</summary>
public enum PostOutcome
{
    Posted,
    /// <summary>The same Idempotency-Key was already applied — the ORIGINAL movement is returned, unchanged.</summary>
    Replayed,
    /// <summary>Would drive on-hand negative. Refused inside the transaction, against a locked balance.</summary>
    InsufficientStock,
    /// <summary>An Issue against an expired batch. Quarantined stock leaves only by an explicit WriteOff.</summary>
    BatchExpired,
    /// <summary>A batch-tracked item's movement named no batch.</summary>
    BatchRequired,
    ItemNotFound,
    BatchNotFound,
    /// <summary>Adjustment / WriteOff / Count without a reason.</summary>
    ReasonRequired,
}

public sealed record PostResult(PostOutcome Outcome, StockMovement? Movement = null, decimal OnHandAfter = 0, decimal OnHandBefore = 0);

/// <summary>
/// 25.5 (design 42 §5) — posting a movement, atomically and idempotently.
///
/// <para><b>Negative on-hand is impossible, and that is a CONCURRENCY guarantee, not a validation.</b> The
/// balance is read under a row lock inside the same transaction that writes the movement, so two parallel
/// issues of the last unit cannot both pass the check. Reading the balance first and inserting afterwards —
/// the obvious shape — is exactly the interleaving that produces a negative balance nobody can explain, and
/// the platform already learned this on order-consume and dispense; this reuses their discipline.</para>
///
/// <para><b>Idempotency is per INTENT, never per attempt.</b> A double-posted receipt is a phantom stock
/// level, and the ledger has no UPDATE to correct it with — only a compensating movement, which leaves two
/// rows where one belonged. The unique index on (tenant_id, idempotency_key) is the authority; the read below
/// is a faster, friendlier path to the same answer.</para>
/// </summary>
public sealed class MovementService(InventoryDbContext db, RlsContext rls, TimeProvider clock)
{
    public async Task<PostResult> PostAsync(
        Guid branchId, Guid itemId, Guid? batchId, MovementKind kind, decimal magnitude,
        string? reason, string actor, string idempotencyKey, DateOnly today,
        Guid? transferRef = null, Guid? counterpartyBranchId = null,
        CancellationToken ct = default)
    {
        var tenant = rls.TenantId ?? "";

        // Fast path for a replay. The unique index still decides — see the catch below — but answering from a
        // read means the common case does not depend on provoking a constraint violation.
        var existing = await db.Movements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenant && m.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
            return new PostResult(PostOutcome.Replayed, existing, await OnHandAsync(branchId, itemId, batchId, ct));

        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ItemId == itemId, ct);
        if (item is null) return new PostResult(PostOutcome.ItemNotFound);

        if (StockRules.RequiresReason(kind) && string.IsNullOrWhiteSpace(reason))
            return new PostResult(PostOutcome.ReasonRequired);

        if (StockRules.RequiresBatch(item.IsBatchTracked, batchId))
            return new PostResult(PostOutcome.BatchRequired);

        StockBatch? batch = null;
        if (batchId is { } bid)
        {
            batch = await db.Batches.AsNoTracking().FirstOrDefaultAsync(x => x.BatchId == bid, ct);
            if (batch is null) return new PostResult(PostOutcome.BatchNotFound);
            if (StockRules.RefuseForExpiry(kind, batch.ExpiryDate, today))
                return new PostResult(PostOutcome.BatchExpired);
        }

        var signed = StockRules.ApplySign(kind, magnitude);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // THE LOCK. `SELECT ... FOR UPDATE` over this branch+item+batch's movement rows serialises concurrent
        // posts for the same stock line without locking the whole table — two clinics issuing different items
        // do not queue behind one another. Rows may not exist yet (a first receipt), which is why the balance
        // is summed separately: a lock over zero rows locks nothing, and that is correct, because a first
        // receipt cannot go negative.
        var onHandBefore = await LockedOnHandAsync(branchId, itemId, batchId, ct);

        if (StockRules.ReducesStock(kind) && StockRules.WouldGoNegative(onHandBefore, signed))
        {
            await tx.RollbackAsync(ct);
            return new PostResult(PostOutcome.InsufficientStock, OnHandBefore: onHandBefore);
        }

        var now = clock.GetUtcNow();
        var movement = new StockMovement
        {
            MovementId = Guid.NewGuid(), TenantId = tenant, BranchId = branchId, ItemId = itemId,
            BatchId = batchId, Kind = kind, Quantity = signed, Reason = reason?.Trim(),
            TransferRef = transferRef, CounterpartyBranchId = counterpartyBranchId,
            Actor = actor, OccurredAt = now, IdempotencyKey = idempotencyKey, CreatedAt = now,
        };
        db.Movements.Add(movement);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique index is the authority on idempotency: two concurrent attempts with one key reach
            // here and exactly one survives. The loser reports the WINNER's row, so both callers see the same
            // outcome — which is the whole point of an idempotency key.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await db.Movements.AsNoTracking()
                .FirstOrDefaultAsync(m => m.TenantId == tenant && m.IdempotencyKey == idempotencyKey, ct);
            if (winner is not null)
                return new PostResult(PostOutcome.Replayed, winner, await OnHandAsync(branchId, itemId, batchId, ct));
            throw;
        }

        return new PostResult(PostOutcome.Posted, movement, onHandBefore + signed, onHandBefore);
    }

    /// <summary>On-hand for one stock line: SUM over the ledger. There is no column to read instead.</summary>
    public async Task<decimal> OnHandAsync(Guid branchId, Guid itemId, Guid? batchId, CancellationToken ct = default)
    {
        var q = db.Movements.AsNoTracking().Where(m => m.BranchId == branchId && m.ItemId == itemId);
        if (batchId is { } b) q = q.Where(m => m.BatchId == b);
        return await q.SumAsync(m => (decimal?)m.Quantity, ct) ?? 0m;
    }

    /// <summary>
    /// Serialise concurrent posters for ONE stock line, then compute its balance.
    ///
    /// <para><b>This started as <c>SELECT ... FOR UPDATE</c> over the movement rows, and the concurrency test
    /// caught it being wrong.</b> A row lock protects rows that EXIST; it does nothing about a concurrent
    /// INSERT. Two callers issuing the last unit each locked the same receipt row, each computed on-hand = 1
    /// against a snapshot taken before the other's insert, and both were allowed — leaving −1 on the shelf.
    /// That is the classic phantom, and on a derived balance there is no balance ROW to lock instead, because
    /// not storing one is the whole design.</para>
    ///
    /// <para>So the lock is taken on the stock LINE as a concept: a transaction-scoped advisory lock keyed on
    /// (branch, item, batch). It is held until commit or rollback, needs no row to exist, and blocks the
    /// second poster until the first has committed its insert — so the second reads a balance that includes
    /// it. Two clinics issuing different items still do not contend, because the key differs.</para>
    /// </summary>
    private async Task<decimal> LockedOnHandAsync(Guid branchId, Guid itemId, Guid? batchId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            var lockKey = $"{branchId:N}:{itemId:N}:{batchId?.ToString("N") ?? "-"}";

            await using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                // hashtextextended gives a stable bigint from the composite key; _xact_ scopes the lock to
                // this transaction so it is released on commit OR rollback with nothing to clean up.
                lockCmd.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))";
                var kp = lockCmd.CreateParameter();
                kp.ParameterName = "k";
                kp.Value = lockKey;
                lockCmd.Parameters.Add(kp);
                await lockCmd.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = batchId is null
                ? "SELECT COALESCE(SUM(quantity), 0) FROM inventory.stock_movement WHERE branch_id = @b AND item_id = @i"
                : "SELECT COALESCE(SUM(quantity), 0) FROM inventory.stock_movement WHERE branch_id = @b AND item_id = @i AND batch_id = @t";

            Add(cmd, "b", branchId);
            Add(cmd, "i", itemId);
            if (batchId is { } bid) Add(cmd, "t", bid);

            var scalar = await cmd.ExecuteScalarAsync(ct);
            return scalar is null or DBNull ? 0m : Convert.ToDecimal(scalar, CultureInfo.InvariantCulture);
        }
        finally { if (opened) await conn.CloseAsync(); }

        static void Add(System.Data.Common.DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddInventoryInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        services.AddHbmpRls();
        services.AddDbContext<InventoryDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Inventory")
                        ?? throw new InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));
        services.AddScoped<MovementService>();
        return services;
    }
}
