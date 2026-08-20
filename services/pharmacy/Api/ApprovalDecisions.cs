using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>The wire shape approvals-service publishes on <c>approvals.events</c> when a request is settled,
/// read off <see cref="ApprovalDecisionFeed"/>. The orders twin of this record is identical by design — one
/// event, two owners.</summary>
public sealed record ApprovalDecisionMessage(
    string? TenantId,
    Guid AuthorizationId,
    string? AuthNo,
    /// <summary><c>Prescription</c> here; anything else belongs to another service and is ignored, not refused.</summary>
    string? Source,
    /// <summary>The prescription id this decision is about.</summary>
    string? SourceRef,
    /// <summary>True for approve / partially-approve / override / emergency-approve; false for reject.</summary>
    bool ReleasesDownstream,
    /// <summary>Set only on a PARTIAL approval: the strict subset of requested drugs the reviewer allowed.</summary>
    IReadOnlyList<string>? ApprovedScope,
    bool BreakGlass,
    /// <summary>
    /// The reviewer who decided. Not decoration: a line cancelled by a partial approval must record WHO
    /// cancelled it (<c>ck_rx_line_amendment_attributed</c>) — "a line that left the live set says why, who
    /// and when, or it did not leave it". <c>Guid.Empty</c> when approvals could not parse the reviewer's
    /// subject as a uuid, which is the same fallback its own decision ledger uses.
    /// </summary>
    Guid ReviewerId);

public enum ApprovalApplyOutcome
{
    /// <summary>The prescription is Approved, and therefore dispensable (23 §3).</summary>
    Released,
    /// <summary>The prescription was rejected and is now terminal.</summary>
    Rejected,
    /// <summary>Not this service's decision (an order), or not a prescription we hold. Acked, not applied.</summary>
    NotOurs,
    /// <summary>
    /// The prescription is no longer waiting on a decision — cancelled meanwhile, or already released by an
    /// earlier delivery of the same decision. Acked; there is simply nothing left to move.
    /// </summary>
    NotWaiting,
}

public sealed record ApprovalApplyResult(ApprovalApplyOutcome Outcome, Guid? PrescriptionId, string? RxNo, string? Detail);

/// <summary>
/// Applies an authorization decision to the prescription that was waiting for it — the RETURN leg of the
/// prior-authorization saga, medication side.
/// </summary>
/// <remarks>
/// <para><b>What was missing, and it was the sharpest gap in the chain.</b>
/// <see cref="PrescriptionWorkflow.IsDispensable"/> admits only <c>Approved</c> and
/// <c>PartiallyDispensed</c>, and the ONLY path that ever set a prescription Approved was the auto-route at
/// creation — for scripts that needed no approval at all. Nothing consumed <c>approvals.events</c>. So a
/// prescription that WAS sent for approval could never become dispensable, whatever the reviewer decided:
/// the counter refused it, correctly, forever, and the reviewer's screen said Approved.</para>
/// <para><b>A partial approval CANCELS the drugs the reviewer did not allow</b> rather than refusing the
/// script. The medication equivalent of narrowing a quantity: a three-drug script with one refusal is two
/// drugs the patient should collect today, and sending them away with nothing because one item was declined
/// is the outcome partial approval exists to avoid. Only <c>Active</c> lines are touched — a line already
/// (partly) dispensed on an earlier round cannot be un-dispensed by a later decision.</para>
/// <para><b>Rejection changes the status and NOT the lines</b>, for the reason the orders twin records:
/// the lines were not withdrawn, the request was refused, and <c>IsDispensable</c> already excludes
/// Rejected.</para>
/// </remarks>
public sealed class PrescriptionApprovalApplier(PharmacyDbContext db, IOutbox outbox, TimeProvider clock)
{
    public async Task<ApprovalApplyResult> ApplyAsync(ApprovalDecisionMessage msg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(msg);

        // Not a refusal. Each decision queue receives every decision and filters by source — see
        // ApprovalDecisionFeed for why the relay does not route by payload.
        if (!string.Equals(msg.Source, "Prescription", StringComparison.Ordinal))
            return new(ApprovalApplyOutcome.NotOurs, null, null, $"source \"{msg.Source}\"");
        if (!Guid.TryParse(msg.SourceRef, out var rxId))
            return new(ApprovalApplyOutcome.NotOurs, null, null, "sourceRef is not a prescription id");

        var rx = await db.Prescriptions.Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
        if (rx is null)
            return new(ApprovalApplyOutcome.NotOurs, rxId, null, "no such prescription in this tenant");

        var target = msg.ReleasesDownstream ? RxStatus.Approved : RxStatus.Rejected;
        if (!PrescriptionWorkflow.CanTransition(rx.Status, target))
            return new(ApprovalApplyOutcome.NotWaiting, rxId, rx.RxNo, $"prescription is {rx.Status}");

        var before = rx.Status;
        rx.AuthorizationId = msg.AuthorizationId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (msg.ReleasesDownstream)
        {
            // Distinguished by whether a scope was sent at all, NOT by counting codes: approvals validates a
            // partial scope as a STRICT subset, so an absent list means "no narrowing was decided" and
            // treating it as "nothing approved" would empty the whole script on a missing field.
            if (msg.ApprovedScope is { Count: > 0 } scope)
            {
                var allowed = scope.ToHashSet(StringComparer.Ordinal);
                var now = clock.GetUtcNow();
                foreach (var line in rx.Lines.Where(l => l.Status == RxLineStatus.Active))
                {
                    if (allowed.Contains(line.DrugId.ToString())) continue;
                    line.Status = RxLineStatus.Cancelled;
                    // WHY, WHO and WHEN, or the database refuses the write
                    // (ck_rx_line_amendment_attributed). The reviewer is the actor: this is their decision,
                    // and attributing it to a background service would put a machine's name on the row a
                    // dispute is read back from.
                    line.AmendmentReasonCode = "not-in-approved-scope";
                    line.AmendedBy = msg.ReviewerId;
                    line.AmendedAt = now;
                }
            }

            rx.Status = RxStatus.Approved;
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("RxApproved", "pharmacy.events",
                new
                {
                    tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo,
                    beneficiaryId = rx.BeneficiaryId, encounterId = rx.EncounterId,
                    authorizationId = msg.AuthorizationId, authNo = msg.AuthNo,
                    approvedScope = msg.ApprovedScope, breakGlass = msg.BreakGlass,
                    // The SAME event type the auto-route emits, with the flag that separates them. A reviewer
                    // said yes here; routing said "no decision needed" there. Two event names for one fact
                    // would make every consumer handle both to answer "is this script live?".
                    auto = false,
                }, ct);
            await tx.CommitAsync(ct);

            return new(ApprovalApplyOutcome.Released, rxId, rx.RxNo, before.ToString());
        }

        rx.Status = RxStatus.Rejected;
        await db.SaveChangesAsync(ct);
        await outbox.EnqueueAsync("RxRejected", "pharmacy.events",
            new
            {
                tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo,
                beneficiaryId = rx.BeneficiaryId, encounterId = rx.EncounterId,
                authorizationId = msg.AuthorizationId, authNo = msg.AuthNo,
            }, ct);
        await tx.CommitAsync(ct);

        return new(ApprovalApplyOutcome.Rejected, rxId, rx.RxNo, before.ToString());
    }
}
