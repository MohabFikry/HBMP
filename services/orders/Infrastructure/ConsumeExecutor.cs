using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Infrastructure;

/// <summary>The outcome of an atomic consume attempt. <c>Applied</c> and <c>Replayed</c> succeed; the rest map to
/// problem responses at the edge. <c>Conflict</c> means another racer won the line's version — re-read and retry.</summary>
public enum ConsumeOutcome
{
    Applied, Replayed, Conflict, NotFound, AlreadyUsed, OverConsume, OrderNotConsumable, LineNotFound, InvalidQuantity,
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
        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
        if (order is null) return ConsumeResult.Fail(ConsumeOutcome.NotFound);

        var keyPrefix = idempotencyKey + "::";

        // (3) Idempotent replay: this key already produced fulfillment rows → return them unchanged.
        var prior = await db.Fulfillments.AsNoTracking().Where(f => f.IdempotencyKey.StartsWith(keyPrefix)).ToListAsync(ct);
        if (prior.Count > 0) return new ConsumeResult(ConsumeOutcome.Replayed, order, prior);

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
                Quantity = r.Quantity, IdempotencyKey = keyPrefix + line.OrderLineId, ConsumedAt = now, ConsumedBy = actorId,
            };
            fulfillments.Add(f);
            db.Fulfillments.Add(f);
        }
        var newOrderStatus = OrderConsume.RecomputeOrderStatus(order);

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
            var fresh = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.OrderId == orderId, ct);
            return new ConsumeResult(ConsumeOutcome.Replayed, fresh, winner);
        }

        if (newOrderStatus != order.Status)
        {
            // Order status is updated out of the line's optimistic guard so concurrent consumes of DIFFERENT lines
            // never falsely collide on the order row.
            await db.Orders.Where(o => o.OrderId == orderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, newOrderStatus), ct);
            order.Status = newOrderStatus;
        }

        if (insideTransaction is not null) await insideTransaction(order, fulfillments, ct);
        await tx.CommitAsync(ct);
        return new ConsumeResult(ConsumeOutcome.Applied, order, fulfillments);
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
