using Mersal.Events;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Infrastructure;

/// <summary>The outcome of an atomic consume attempt. <c>Applied</c> and <c>Replayed</c> succeed; the rest map to
/// problem responses at the edge. <c>Conflict</c> means another racer won the line's version — re-read and retry.</summary>
public enum ConsumeOutcome
{
    Applied, Replayed, Conflict, NotFound, AlreadyUsed, OverConsume, OrderNotConsumable, LineNotFound, InvalidQuantity,
    /// <summary>18.A3 — the header is empty, over-length, or contains the reserved <c>::</c> separator.</summary>
    InvalidIdempotencyKey,
    /// <summary>18.A3 — the key was already used for a DIFFERENT request body. Answering it with the
    /// original fulfillments would tell the caller work had been done that never happened.</summary>
    IdempotencyKeyReuse,
}

public sealed record ConsumeResult(ConsumeOutcome Outcome, InvestigationOrder? Order, IReadOnlyList<OrderFulfillment> Fulfillments)
{
    public static ConsumeResult Fail(ConsumeOutcome outcome) => new(outcome, null, []);
}

/// <summary>The heart of phase 5 in one place (23-state-machines §2 "Atomic-consume guard") so the endpoint and the
/// concurrency tests exercise the SAME code. Three mechanisms combine, all required:
/// <list type="number">
/// <item>append-only <c>order_fulfillment</c> insert per line, keyed by a UNIQUE idempotency key;</item>
/// <item>optimistic concurrency on the line's <c>xmin</c> — the UPDATE lands only if the line hasn't moved, so
/// exactly one of N racers wins (EF raises <see cref="DbUpdateConcurrencyException"/> for the losers);</item>
/// <item>idempotent replay — the same key returns the prior fulfillment rows with no new row/state change.</item>
/// </list>
/// The DB CHECK (0 ≤ consumed ≤ ordered) is the final backstop. Line + order status recompute happen in one
/// transaction; a caller may inject outbox writes via <paramref name="insideTransaction"/> so events publish
/// atomically with the state change.</summary>
public sealed class ConsumeExecutor(OrdersDbContext db)
{
    public async Task<ConsumeResult> ConsumeAsync(
        Guid orderId, string idempotencyKey, Guid performingProviderId, Guid actorId,
        IReadOnlyList<ConsumeLineRequest> requests, DateTimeOffset now,
        Func<InvestigationOrder, IReadOnlyList<OrderFulfillment>, CancellationToken, Task>? insideTransaction = null,
        CancellationToken ct = default)
    {
        // 18.A3: the reserved "::" separator may not appear in a caller's key, which is what makes the
        // per-line composed key (and therefore the prefix match below) unambiguous.
        if (IdempotencyKeyRules.Validate(idempotencyKey) is not null)
            return ConsumeResult.Fail(ConsumeOutcome.InvalidIdempotencyKey);

        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
        if (order is null) return ConsumeResult.Fail(ConsumeOutcome.NotFound);

        var keyPrefix = idempotencyKey + IdempotencyKeyRules.Separator;
        var requestHash = HashRequest(orderId, requests);

        // (3) Idempotent replay: this key already produced fulfillment rows → return them unchanged,
        // but ONLY if it was the same request. A key reused with a different body is rejected.
        var prior = await db.Fulfillments.AsNoTracking().Where(f => f.IdempotencyKey.StartsWith(keyPrefix)).ToListAsync(ct);
        if (prior.Count > 0)
            return prior.All(f => IdempotencyKeyRules.Matches(f.RequestHash, requestHash))
                ? new ConsumeResult(ConsumeOutcome.Replayed, order, prior)
                : ConsumeResult.Fail(ConsumeOutcome.IdempotencyKeyReuse);

        var error = OrderConsume.Validate(order, requests);
        if (error != ConsumeError.None) return ConsumeResult.Fail(Map(error));

        var fulfillments = new List<OrderFulfillment>();
        foreach (var r in requests)
        {
            var line = order.Lines.First(l => l.OrderLineId == r.OrderLineId);
            line.QuantityConsumed += r.Quantity;
            line.Status = line.QuantityConsumed >= line.QuantityOrdered ? OrderLineStatus.Completed : OrderLineStatus.PartiallyUsed;
            var f = new OrderFulfillment
            {
                FulfillmentId = Guid.NewGuid(), OrderLineId = line.OrderLineId, PerformingProviderId = performingProviderId,
                Quantity = r.Quantity, IdempotencyKey = keyPrefix + line.OrderLineId, RequestHash = requestHash,
                ConsumedAt = now, ConsumedBy = actorId,
            };
            fulfillments.Add(f);
            db.Fulfillments.Add(f);
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // (1)+(2) insert fulfillments + UPDATE order_line ... WHERE xmin=@old, atomically.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return ConsumeResult.Fail(ConsumeOutcome.Conflict);   // a concurrent consume won the line's version
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            // A concurrent request with the SAME key won the insert race → idempotent: return its outcome.
            var winner = await db.Fulfillments.AsNoTracking().Where(f => f.IdempotencyKey.StartsWith(keyPrefix)).ToListAsync(ct);
            if (!winner.All(f => IdempotencyKeyRules.Matches(f.RequestHash, requestHash)))
                return ConsumeResult.Fail(ConsumeOutcome.IdempotencyKeyReuse);
            var fresh = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.OrderId == orderId, ct);
            return new ConsumeResult(ConsumeOutcome.Replayed, fresh, winner);
        }

        // 18.A3 (audit R2 X7): the aggregate status is recomputed from the lines as they are NOW, read
        // back inside this transaction — not from the in-memory graph loaded before the racers ran — and
        // applied with a guarded UPDATE (WHERE status = @expected) with bounded retry. Two racers on
        // DIFFERENT lines used to both write PartiallyUsed from their own stale snapshot, stranding a
        // fully-consumed order in PartiallyUsed forever so OrderCompleted never emitted. The per-line
        // xmin guard above is untouched — this only fixes the roll-up.
        order.Status = await ApplyAggregateStatusAsync(orderId, ct);

        if (insideTransaction is not null) await insideTransaction(order, fulfillments, ct);
        await tx.CommitAsync(ct);
        return new ConsumeResult(ConsumeOutcome.Applied, order, fulfillments);
    }

    /// <summary>Canonical hash of what this consume asks for: the order plus every (line, quantity),
    /// sorted so two orderings of the same work hash alike.</summary>
    private static string HashRequest(Guid orderId, IReadOnlyList<ConsumeLineRequest> requests)
    {
        var parts = new List<string> { orderId.ToString() };
        foreach (var r in requests.OrderBy(x => x.OrderLineId))
        {
            parts.Add(r.OrderLineId.ToString());
            parts.Add(IdempotencyKeyRules.Amount(r.Quantity));
        }
        return IdempotencyKeyRules.Hash([.. parts]);
    }

    /// <summary>Re-read the order's lines inside the transaction, recompute the aggregate status from
    /// them, and apply it with a guarded UPDATE. The guard makes the write a compare-and-set: a racer
    /// that moved the order between our read and our write loses, and we retry against the value it
    /// wrote. Returns the status the order actually holds.</summary>
    private async Task<OrderStatus> ApplyAggregateStatusAsync(Guid orderId, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; ; attempt++)
        {
            var fresh = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .FirstAsync(o => o.OrderId == orderId, ct);
            var current = fresh.Status;
            var recomputed = OrderConsume.RecomputeOrderStatus(fresh);
            if (recomputed == current) return current;

            var affected = await db.Orders
                .Where(o => o.OrderId == orderId && o.Status == current)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, recomputed), ct);
            if (affected == 1) return recomputed;

            // Lost the compare-and-set: another consume moved the order. Re-read and try again. The
            // recompute is a pure function of the line rows, so this converges.
            if (attempt >= maxAttempts - 1)
                return (await db.Orders.AsNoTracking().Where(o => o.OrderId == orderId)
                    .Select(o => o.Status).FirstAsync(ct));
        }
    }

    private static ConsumeOutcome Map(ConsumeError error) => error switch
    {
        ConsumeError.InvalidQuantity => ConsumeOutcome.InvalidQuantity,
        ConsumeError.LineNotFound => ConsumeOutcome.LineNotFound,
        ConsumeError.AlreadyUsed => ConsumeOutcome.AlreadyUsed,
        ConsumeError.OverConsume => ConsumeOutcome.OverConsume,
        ConsumeError.OrderNotConsumable => ConsumeOutcome.OrderNotConsumable,
        _ => ConsumeOutcome.InvalidQuantity,
    };

    /// <summary>True when a save failed on a UNIQUE violation (Postgres SQLSTATE 23505) — the idempotency-key insert
    /// lost a race. Read via reflection to avoid a hard Npgsql compile dependency here.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return true;
        return false;
    }
}
