using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>The wire shape approvals-service publishes on <c>approvals.events</c> when a request is settled,
/// read off <see cref="ApprovalDecisionFeed"/>.</summary>
/// <remarks>
/// A subset of the published payload: <c>tatSeconds</c>, <c>slaBreached</c> and <c>priority</c> are
/// deliberately not modelled. They belong to the read model, and an order does not become more or less
/// actionable because a reviewer was slow. <c>reviewerId</c> IS modelled, for the reason its own summary
/// gives — the database will not accept a cancelled line without a person on it.
/// </remarks>
public sealed record ApprovalDecisionMessage(
    string? TenantId,
    Guid AuthorizationId,
    string? AuthNo,
    /// <summary><c>OrderLine</c> here; anything else belongs to another service and is ignored, not refused.</summary>
    string? Source,
    /// <summary>The order id this decision is about.</summary>
    string? SourceRef,
    /// <summary>True for approve / partially-approve / override / emergency-approve; false for reject.</summary>
    bool ReleasesDownstream,
    /// <summary>Set only on a PARTIAL approval: the strict subset of requested codes the reviewer allowed.</summary>
    IReadOnlyList<string>? ApprovedScope,
    bool BreakGlass,
    /// <summary>
    /// The reviewer who decided. Not decoration: a line cancelled by a partial approval must record WHO
    /// cancelled it (<c>ck_order_line_amendment_attributed</c>) — "a line that left the live set says why, who
    /// and when, or it did not leave it". <c>Guid.Empty</c> when approvals could not parse the reviewer's
    /// subject as a uuid, which is the same fallback its own decision ledger uses.
    /// </summary>
    Guid ReviewerId);

public enum ApprovalApplyOutcome
{
    /// <summary>The order was released: Approved, then Active (23 §2).</summary>
    Released,
    /// <summary>The order was rejected and is now terminal.</summary>
    Rejected,
    /// <summary>Not this service's decision (a prescription), or not an order we hold. Acked, not applied.</summary>
    NotOurs,
    /// <summary>
    /// The order is no longer waiting on a decision — cancelled meanwhile, or already released by an earlier
    /// delivery of the same decision. Acked; the decision stands, there is simply nothing left to move.
    /// </summary>
    NotWaiting,
}

public sealed record ApprovalApplyResult(ApprovalApplyOutcome Outcome, Guid? OrderId, string? OrderNo, string? Detail);

/// <summary>
/// Applies an authorization decision to the order that was waiting for it — the RETURN leg of the
/// prior-authorization saga.
/// </summary>
/// <remarks>
/// <para><b>What was missing.</b> <see cref="OrderWorkflow"/> has declared
/// <c>PendingApproval → Approved → Active</c> since phase 4 and nothing in the platform executed it. A gated
/// order sat in PendingApproval indefinitely whatever the reviewer decided, and a REJECTED one looked
/// identical to one still in the queue — so the only honest thing any screen could say about either was
/// "waiting".</para>
/// <para><b>Approved goes all the way to Active, in one transaction.</b> 23 §2 lists them as two rows
/// (<c>approve</c> by the approval team, then <c>activate</c> by orders-service) and both events are emitted,
/// but there is no state anyone can act on in between and no second trigger to wait for: leaving an order
/// Approved-but-not-Active would mean a technician with the patient present seeing nothing in their queue,
/// which is the failure this whole path exists to remove.</para>
/// <para><b>A partial approval NARROWS rather than refuses.</b> Lines whose code the reviewer allowed are
/// untouched; the refused ones are cancelled, so a two-test order with one refusal is still one test the
/// patient has today. See the note at the call site for why this is a code-level act and not a quantity-level
/// one — the decision contract has no quantity in it, and the signed-content trigger would refuse one.</para>
/// <para><b>Rejection changes the status and NOT the lines.</b> The provider queue is a live query over
/// order status (<c>Queue.AvailableOrders</c> admits Active / PartiallyUsed / Expired only), so Rejected
/// removes the order from every worklist in the same transaction. Cancelling the lines as well would record
/// a line-level act that nobody performed — the lines were not withdrawn, the request was refused.</para>
/// </remarks>
public sealed class OrderApprovalApplier(OrdersDbContext db, IOutbox outbox, TimeProvider clock)
{
    public async Task<ApprovalApplyResult> ApplyAsync(ApprovalDecisionMessage msg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(msg);

        // Not a refusal. Each decision queue receives every decision and filters by source — see
        // ApprovalDecisionFeed for why the relay does not route by payload.
        if (!string.Equals(msg.Source, "OrderLine", StringComparison.Ordinal))
            return new(ApprovalApplyOutcome.NotOurs, null, null, $"source \"{msg.Source}\"");
        if (!Guid.TryParse(msg.SourceRef, out var orderId))
            return new(ApprovalApplyOutcome.NotOurs, null, null, "sourceRef is not an order id");

        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
        if (order is null)
            return new(ApprovalApplyOutcome.NotOurs, orderId, null, "no such order in this tenant");

        var target = msg.ReleasesDownstream ? OrderStatus.Approved : OrderStatus.Rejected;
        if (!OrderWorkflow.CanTransition(order.Status, target))
            return new(ApprovalApplyOutcome.NotWaiting, orderId, order.OrderNo, $"order is {order.Status}");

        var before = order.Status;
        order.AuthorizationId = msg.AuthorizationId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (msg.ReleasesDownstream)
        {
            /*
             * A PARTIAL APPROVAL IS EXPRESSED AT THE CODE LEVEL, so it is applied at the code level.
             *
             * The decision contract carries a list of CODES and nothing else — `DecisionRules.ValidatePartialScope`
             * checks the reviewer's scope is a strict, non-empty subset of the requested codes, and there is no
             * quantity anywhere in it. So "partially approved" means these codes yes, those no; the refused
             * lines are cancelled and the allowed ones are untouched.
             *
             * NOT `ProcedureSessions.ApplyApproval`, despite its doc saying it is "applied when an approval
             * decision is recorded". That method narrows `QuantityOrdered`, and the phase-30 signed-content
             * trigger (orders 0013) freezes that column against in-place update: the write raises
             * `order line ... is signed clinical content and can never be edited in place`. It has only ever
             * been called on detached objects in its own tests, and a quantity-level approval is not something
             * any decision path can currently produce.
             *
             * Distinguished by whether a scope was sent AT ALL, not by counting codes: approvals sends one only
             * for a partial approval, so an absent list means "nothing was narrowed" and reading it as
             * "nothing was approved" would cancel every line of a fully approved order on a missing field.
             */
            if (msg.ApprovedScope is { Count: > 0 } scope)
            {
                var allowed = scope.ToHashSet(StringComparer.Ordinal);
                var now = clock.GetUtcNow();
                // Active only. A PartiallyUsed line has had a sample taken, and cancelling it would imply
                // un-delivering an attendance that happened; that overage is a case for the approvals team.
                foreach (var line in order.Lines.Where(l => l.Status == OrderLineStatus.Active))
                {
                    if (allowed.Contains(line.Code)) continue;
                    line.Status = OrderLineStatus.Cancelled;
                    // WHY, WHO and WHEN, or the database refuses the write
                    // (ck_order_line_amendment_attributed): "a line that left the live set says why, who and
                    // when, or it did not leave it". The reviewer is the actor — this is their decision, not
                    // the consumer's, and attributing it to a background service would put a machine's name
                    // on the row a dispute is read back from.
                    line.AmendmentReasonCode = "not-in-approved-scope";
                    line.AmendedBy = msg.ReviewerId;
                    line.AmendedAt = now;
                }
            }

            order.Status = OrderStatus.Approved;
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("OrderApproved", "orders.events",
                new
                {
                    tenantId = order.TenantId, orderId = order.OrderId, order.OrderNo,
                    beneficiaryId = order.BeneficiaryId, encounterId = order.EncounterId,
                    authorizationId = msg.AuthorizationId, authNo = msg.AuthNo,
                    approvedScope = msg.ApprovedScope, breakGlass = msg.BreakGlass,
                }, ct);

            // 23 §2 `Approved --activate--> Active`, by orders-service, with nothing to wait for in between.
            order.Status = OrderStatus.Active;
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("OrderActivated", "orders.events",
                new { tenantId = order.TenantId, orderId = order.OrderId, order.OrderNo }, ct);
            await tx.CommitAsync(ct);

            return new(ApprovalApplyOutcome.Released, orderId, order.OrderNo, before.ToString());
        }

        order.Status = OrderStatus.Rejected;
        await db.SaveChangesAsync(ct);
        await outbox.EnqueueAsync("OrderRejected", "orders.events",
            new
            {
                tenantId = order.TenantId, orderId = order.OrderId, order.OrderNo,
                beneficiaryId = order.BeneficiaryId, encounterId = order.EncounterId,
                authorizationId = msg.AuthorizationId, authNo = msg.AuthNo,
            }, ct);
        await tx.CommitAsync(ct);

        return new(ApprovalApplyOutcome.Rejected, orderId, order.OrderNo, before.ToString());
    }
}
