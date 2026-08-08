using Mersal.Amendment;
using Mersal.Events;
using Npgsql;
using NpgsqlTypes;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Infrastructure;

/// <summary>The outcome of a cancel or amend attempt. <c>Applied</c> and <c>Replayed</c> succeed; the rest
/// map to problem responses at the edge.</summary>
public enum AmendOutcome
{
    Applied, Replayed, NotFound, LineNotFound,
    /// <summary>Another racer moved the line between our read and our write. Distinct from
    /// <see cref="AlreadyTerminal"/>, which is the same fact discovered BEFORE we tried.</summary>
    Conflict,
    AlreadyTerminal, OrderNotAmendable, Expired, BelowConsumed, InvalidQuantity, NoChange,
    /// <summary>The reason code is not in the vocabulary, or not valid for this kind of order.</summary>
    InvalidReason,
    InvalidIdempotencyKey, IdempotencyKeyReuse,
}

/// <summary>The coded reason plus its optional sentence. Both, never one — see <see cref="AmendmentReasons"/>.</summary>
public sealed record AmendReason(string Code, string? Text);

/// <summary>
/// What happened to the line instead, so the refusal can say it.
///
/// <para>Design 46 §2: "the response must say WHAT happened ('line 2 was dispensed at 14:32 by Maadi
/// Pharmacy'), not a generic conflict. A doctor who is told 'someone else changed this' and nothing else
/// will simply retry" — and a retry after a dispense is how a cancelled-then-dispensed drug happens.</para>
/// </summary>
public sealed record AmendConflict(
    string What, DateTimeOffset? When, Guid? PerformedByProviderId,
    string? ReasonCode, string? ReasonText);

public sealed record AmendResult(
    AmendOutcome Outcome, Guid? AmendmentId = null, Guid? NewLineId = null, AmendConflict? Conflict = null,
    /// <summary>30.4 — what the amendment did to the authorisation the order carried (design 46 §5).</summary>
    AuthorizationImpact Impact = AuthorizationImpact.NotAuthorized)
{
    public static AmendResult Fail(AmendOutcome outcome, AmendConflict? conflict = null) =>
        new(outcome, Conflict: conflict);
}

/// <summary>
/// 30.2 — the guarded cancel/amend transition (design 46 §2), the correctness core of this phase.
///
/// <para><b>Never read-then-write.</b> "Not yet consumed" is not a state you can read and then act on: a
/// technician may begin between the doctor's click and the server's write. So the state check and the write
/// are ONE statement — <c>UPDATE … WHERE line_id = @id AND status IN (amendable) AND xmin = @expected</c> —
/// and zero rows affected means somebody got there first.</para>
///
/// <para><b>The same three mechanisms as <see cref="ConsumeExecutor"/>, deliberately not a fourth.</b> The
/// append-only <c>line_amendment</c> row keyed by a UNIQUE idempotency key; the line's <c>xmin</c> as the
/// optimistic-concurrency guard; and idempotent replay of the same key. Those took several phases to get
/// right on the consume path and this reuses them rather than re-deriving them.</para>
///
/// <para><b>Why raw SQL for the guard.</b> EF's <c>SaveChanges</c> would emit the <c>xmin</c> predicate but
/// not the status one, and the status predicate is what makes the statement say out loud which states are
/// amendable. Both together are belt and braces — any status change bumps <c>xmin</c>, so either alone would
/// do — and a guard you can read in one line is a guard the next person will not weaken by accident.</para>
/// </summary>
public sealed class AmendExecutor(OrdersDbContext db)
{
    /// <summary>Statuses a line may be cancelled or amended FROM. Terminal states are absent by construction.</summary>
    private const string AmendableStates = "'Active','PartiallyUsed'";

    /// <summary>
    /// THE guarded transition, written out in full so it can be read in one sitting. The status predicate and
    /// the <c>xmin</c> predicate are both in the WHERE: either alone would be sufficient (any status change
    /// bumps <c>xmin</c>), and having both means the statement says out loud which states are amendable
    /// rather than leaving that to a comment somewhere else.
    /// </summary>
    private const string GuardedTransitionSql =
        """
        UPDATE orders.order_line
           SET status                = @status,
               amendment_reason_code = @code,
               amendment_reason_text = @text,
               amended_by            = @actor,
               amended_at            = @at,
               superseded_by_id      = @successor
         WHERE order_line_id = @line
           AND status IN (
        """ + AmendableStates + """
        )
           AND xmin::text = @expected
        """;

    public Task<AmendResult> CancelLineAsync(
        Guid orderId, Guid lineId, string idempotencyKey, AmendReason reason, Guid actor, string? actorDisplay,
        DateTimeOffset now, Func<InvestigationOrder, OrderLine, LineAmendmentRecord, CancellationToken, Task>? insideTransaction = null,
        CancellationToken ct = default) =>
        ApplyAsync(orderId, lineId, idempotencyKey, reason, actor, actorDisplay, now,
            newQuantity: null, insideTransaction, ct);

    public Task<AmendResult> AmendLineQuantityAsync(
        Guid orderId, Guid lineId, string idempotencyKey, decimal newQuantity, AmendReason reason, Guid actor,
        string? actorDisplay, DateTimeOffset now,
        Func<InvestigationOrder, OrderLine, LineAmendmentRecord, CancellationToken, Task>? insideTransaction = null,
        CancellationToken ct = default) =>
        ApplyAsync(orderId, lineId, idempotencyKey, reason, actor, actorDisplay, now,
            newQuantity, insideTransaction, ct);

    private async Task<AmendResult> ApplyAsync(
        Guid orderId, Guid lineId, string idempotencyKey, AmendReason reason, Guid actor, string? actorDisplay,
        DateTimeOffset now, decimal? newQuantity,
        Func<InvestigationOrder, OrderLine, LineAmendmentRecord, CancellationToken, Task>? insideTransaction,
        CancellationToken ct)
    {
        // Validate the CALLER's key, which is the part before the reserved "::" separator. The whole-order
        // cancel composes a per-line key from it — `{caller}::{lineId}` — exactly as ConsumeExecutor composes
        // its per-line keys, and `Validate` rejects "::" on purpose so that composition stays unambiguous.
        // Validating the composed key here would reject the platform's own convention.
        if (IdempotencyKeyRules.Validate(CallerPortOf(idempotencyKey)) is not null)
            return AmendResult.Fail(AmendOutcome.InvalidIdempotencyKey);
        if (!AmendmentReasons.IsValid(reason.Code, ReasonScope.Order))
            return AmendResult.Fail(AmendOutcome.InvalidReason);

        var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
        if (order is null) return AmendResult.Fail(AmendOutcome.NotFound);

        var line = order.Lines.FirstOrDefault(l => l.OrderLineId == lineId);
        if (line is null) return AmendResult.Fail(AmendOutcome.LineNotFound);

        var requestHash = HashRequest(orderId, lineId, newQuantity, reason);

        // Idempotent replay: this key already produced an amendment → return it unchanged, but ONLY if it was
        // the SAME request. A key reused for a different line or a different reason is rejected, because
        // answering it with the first cancellation would tell the doctor a line had been withdrawn that had
        // not (18.A3's rule, applied here).
        var prior = await db.LineAmendments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, ct);
        if (prior is not null)
            return IdempotencyKeyRules.Matches(prior.RequestHash, requestHash)
                ? new AmendResult(AmendOutcome.Replayed, prior.AmendmentId, prior.NewLineId)
                : AmendResult.Fail(AmendOutcome.IdempotencyKeyReuse);

        // The pure rule, shared with pharmacy and with the doctor's screen (libs/amendment).
        var ctx = new AmendContext(
            HeadAmendable: OrderWorkflow.CanAmendLines(order.Status),
            Expired: order.Status == OrderStatus.Expired || (order.ExpiresAt is { } e && e <= now));
        var subject = new AmendableLine(lineId, line.IsTerminal, line.QuantityOrdered, line.QuantityConsumed);

        var error = newQuantity is { } q
            ? LineAmendability.ForAmend(subject, q, ctx)
            : LineAmendability.ForCancel(subject, ctx);
        if (error != AmendabilityError.None)
            return AmendResult.Fail(Map(error), await DescribeAsync(line, ct));

        var amendmentId = Guid.NewGuid();
        var newLineId = newQuantity is null ? (Guid?)null : Guid.NewGuid();
        var toStatus = newQuantity is null ? nameof(OrderLineStatus.Cancelled) : nameof(OrderLineStatus.Superseded);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // The successor is inserted FIRST: the guarded UPDATE below sets superseded_by_id, and that is a
            // foreign key. If the UPDATE then loses its race the whole transaction rolls back, so no orphan
            // successor survives — which is why the insert is inside the transaction and not before it.
            if (newLineId is { } nid)
                await InsertSuccessorAsync(line, nid, newQuantity!.Value, ct);

            // ---- THE GUARDED TRANSITION. One statement; the check and the write are the same act. ----
            // Explicitly typed parameters, not positional ones: the two nullable columns need a store type
            // even when the value is null, and EF cannot infer one from a bare null.
            var affected = await db.Database.ExecuteSqlRawAsync(GuardedTransitionSql,
                [
                    new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = toStatus },
                    new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = reason.Code },
                    new NpgsqlParameter("text", NpgsqlDbType.Varchar) { Value = (object?)reason.Text ?? DBNull.Value },
                    new NpgsqlParameter("actor", NpgsqlDbType.Uuid) { Value = actor },
                    new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = now },
                    new NpgsqlParameter("successor", NpgsqlDbType.Uuid) { Value = (object?)newLineId ?? DBNull.Value },
                    new NpgsqlParameter("line", NpgsqlDbType.Uuid) { Value = lineId },
                    new NpgsqlParameter("expected", NpgsqlDbType.Text) { Value = line.RowVersion.ToString() },
                ], ct);

            if (affected == 0)
            {
                // Somebody got there first. Roll back (taking any successor row with it) and report WHAT,
                // read fresh — the doctor needs the fact, not the word "conflict".
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                var fresh = await db.OrderLines.AsNoTracking().FirstAsync(l => l.OrderLineId == lineId, ct);
                return AmendResult.Fail(
                    fresh.IsTerminal ? AmendOutcome.AlreadyTerminal : AmendOutcome.Conflict,
                    await DescribeAsync(fresh, ct));
            }

            // ---- 30.4 THE AUTHORISATION QUESTION (design 46 §5) --------------------------------------
            //
            // Answered LOCALLY, from what the order already knows, and deliberately not by asking
            // approvals-service: a doctor correcting a mistake must not be blocked by another service being
            // unreachable. The order carries `authorization_id` (gated or not), and `quantity_ordered` IS the
            // approved quantity — phase 29 set it from the approved scope precisely so the two could be told
            // apart from `requested_quantity`.
            var impact = newQuantity is { } amendedQty
                ? AuthorizationScope.Assess(
                    new AmendedScope(line.Code, amendedQty, null),
                    order.AuthorizationId is null
                        ? null
                        : new ApprovedScope([line.Code], line.QuantityOrdered, null))
                // A CANCELLATION is always within scope: withdrawing something approved cannot exceed what
                // was approved, and sending it back for review would ask a reviewer to re-approve nothing.
                : AuthorizationImpact.WithinApprovedScope;

            var record = new LineAmendmentRecord
            {
                AmendmentId = amendmentId, TenantId = order.TenantId, OrderId = orderId, OrderLineId = lineId,
                NewLineId = newLineId,
                Action = newQuantity is null ? "Cancel" : "Amend",
                FromStatus = line.Status.ToString(), ToStatus = toStatus,
                ReasonCode = reason.Code, ReasonText = reason.Text,
                AmendedBy = actor, AmendedByDisplay = actorDisplay, AmendedAt = now,
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
            };
            db.LineAmendments.Add(record);
            await db.SaveChangesAsync(ct);

            // The aggregate rolls up from its lines as they are NOW — a cancelled last line can complete the
            // order. Guarded and retried for the same reason ConsumeExecutor's is (18.A3 audit R2 X7).
            await ApplyAggregateStatusAsync(orderId, ct);

            if (insideTransaction is not null)
            {
                var updated = await db.OrderLines.AsNoTracking().FirstAsync(l => l.OrderLineId == lineId, ct);
                await insideTransaction(order, updated, record, ct);
            }
            // BEYOND the approved scope: the authorisation's basis no longer holds, so the order goes back
            // for review. Applied AFTER the aggregate roll-up, which recomputes from the lines and would
            // otherwise overwrite it.
            if (impact == AuthorizationImpact.BeyondApprovedScope)
                await db.Orders.Where(o => o.OrderId == orderId
                        && (o.Status == OrderStatus.Active || o.Status == OrderStatus.PartiallyUsed))
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.PendingApproval), ct);

            await tx.CommitAsync(ct);
            return new AmendResult(AmendOutcome.Applied, amendmentId, newLineId, Impact: impact);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent request with the SAME key won the ledger insert → idempotent: return its outcome.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await db.LineAmendments.AsNoTracking()
                .FirstAsync(a => a.IdempotencyKey == idempotencyKey, ct);
            return IdempotencyKeyRules.Matches(winner.RequestHash, requestHash)
                ? new AmendResult(AmendOutcome.Replayed, winner.AmendmentId, winner.NewLineId)
                : AmendResult.Fail(AmendOutcome.IdempotencyKeyReuse);
        }
    }

    /// <summary>
    /// The new version. Every clinical field is COPIED, not re-derived: an amendment changes one thing, and
    /// anything the caller did not change must survive it byte for byte.
    ///
    /// <para><c>QuantityConsumed</c> carries forward. That is invariant 2 in one line — without it a line
    /// with four of six sessions delivered, amended to five, would offer five MORE sessions instead of
    /// one.</para>
    /// </summary>
    private async Task InsertSuccessorAsync(OrderLine original, Guid newLineId, decimal newQuantity, CancellationToken ct)
    {
        var successor = new OrderLine
        {
            OrderLineId = newLineId, TenantId = original.TenantId, OrderId = original.OrderId,
            CodeSystem = original.CodeSystem, Code = original.Code, Description = original.Description,
            ExaminationTypeId = original.ExaminationTypeId, SensitivityLevel = original.SensitivityLevel,
            ProcedureTypeCode = original.ProcedureTypeCode,
            // 31.1 — the new version delivers the same amount at each attendance as the row it replaces.
            // Losing it would silently reset the line to 1-per-session and halve a course's metered total.
            QuantityPerSession = original.QuantityPerSession,
            // requested_quantity must cover the new ordered quantity (ck_order_line_ordered_within_requested).
            // An amendment that RAISES the delivered quantity necessarily raises what was asked for; whether
            // that needs a fresh authorisation is Gate 4's question, not this one's.
            RequestedQuantity = Math.Max(original.RequestedQuantity, newQuantity),
            QuantityOrdered = newQuantity,
            QuantityConsumed = original.QuantityConsumed,
            Status = original.QuantityConsumed >= newQuantity
                ? OrderLineStatus.Completed
                : original.QuantityConsumed > 0 ? OrderLineStatus.PartiallyUsed : OrderLineStatus.Active,
            VersionNo = original.VersionNo + 1,
            SupersedesId = original.OrderLineId,
            RootLineId = original.RootLineId,
        };
        db.OrderLines.Add(successor);
        await db.SaveChangesAsync(ct);
        db.Entry(successor).State = EntityState.Detached;
    }

    /// <summary>What happened to this line instead, in the words the doctor needs.</summary>
    private async Task<AmendConflict?> DescribeAsync(OrderLine line, CancellationToken ct)
    {
        if (line.Status is OrderLineStatus.Cancelled or OrderLineStatus.Superseded)
            return new AmendConflict(line.Status.ToString(), line.AmendedAt, null,
                line.AmendmentReasonCode, line.AmendmentReasonText);

        // Consumed, in whole or in part: the most recent fulfilment is the event the doctor is racing.
        var last = await db.Fulfillments.AsNoTracking()
            .Where(f => f.OrderLineId == line.OrderLineId)
            .OrderByDescending(f => f.ConsumedAt).FirstOrDefaultAsync(ct);
        return last is null ? null : new AmendConflict("Consumed", last.ConsumedAt, last.PerformingProviderId, null, null);
    }

    /// <summary>Canonical hash of what this request asks for, so a key reused for a different line, a
    /// different quantity or a different reason is rejected instead of answered with somebody else's work.</summary>
    /// <summary>The caller-supplied portion of a possibly-composed key: everything before the first
    /// <c>::</c>. See the call site for why.</summary>
    private static string CallerPortOf(string key)
    {
        var at = key.IndexOf(IdempotencyKeyRules.Separator, StringComparison.Ordinal);
        return at < 0 ? key : key[..at];
    }

    private static string HashRequest(Guid orderId, Guid lineId, decimal? newQuantity, AmendReason reason) =>
        IdempotencyKeyRules.Hash(
            orderId.ToString(), lineId.ToString(),
            newQuantity is { } q ? IdempotencyKeyRules.Amount(q) : "cancel",
            reason.Code, reason.Text ?? "-");

    /// <summary>Re-read the lines inside the transaction, recompute the aggregate and apply it as a
    /// compare-and-set with bounded retry — the pattern <see cref="ConsumeExecutor"/> established.</summary>
    private async Task ApplyAggregateStatusAsync(Guid orderId, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var fresh = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .FirstAsync(o => o.OrderId == orderId, ct);
            var current = fresh.Status;
            var recomputed = OrderConsume.RecomputeOrderStatus(fresh);
            if (recomputed == current) return;

            var affected = await db.Orders.Where(o => o.OrderId == orderId && o.Status == current)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, recomputed), ct);
            if (affected == 1) return;
        }
    }

    private static AmendOutcome Map(AmendabilityError error) => error switch
    {
        AmendabilityError.AlreadyTerminal => AmendOutcome.AlreadyTerminal,
        AmendabilityError.OrderNotAmendable => AmendOutcome.OrderNotAmendable,
        AmendabilityError.Expired => AmendOutcome.Expired,
        AmendabilityError.BelowConsumed => AmendOutcome.BelowConsumed,
        AmendabilityError.InvalidQuantity => AmendOutcome.InvalidQuantity,
        AmendabilityError.NoChange => AmendOutcome.NoChange,
        _ => AmendOutcome.Conflict,
    };

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return true;
        return false;
    }
}
