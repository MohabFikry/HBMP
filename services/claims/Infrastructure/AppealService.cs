using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

public enum AppealOutcome { Raised, RoutedToAdjustment, NotFound, NotAppealable }
public sealed record AppealResult(AppealOutcome Outcome, ClaimAppeal? Appeal, Claim? Claim);

/// <summary>Appeals of decided claims (10b.9, 36 §6, 23 §7). Parallel to the authorization InfoRequested/resubmit path:
/// the prior <c>claim_decision</c> thread is NEVER edited or hidden. A live decided claim RE-ENTERS UnderAdjudication
/// (its appealed lines return to Pending so the worklist picks them up) with the appeal linked to the original
/// decision; a claim on an already-SETTLED batch is recorded as <c>RoutedToAdjustment</c> — the settled batch is never
/// reopened and the correction flows as a compensating adjustment/recovery (10b.7) in a later batch. The re-decision's
/// SoD (original decider may not re-decide) is enforced in <see cref="DecisionService"/>.</summary>
public sealed class AppealService(ClaimsDbContext db, TimeProvider clock)
{
    private static readonly ClaimStatus[] Appealable =
        [ClaimStatus.Approved, ClaimStatus.PartiallyApproved, ClaimStatus.Denied];

    public async Task<AppealResult> RaiseAsync(
        string tenantId, string actor, Guid claimId, Guid? lineId, AppellantType appellant, string reason,
        string? actingFor, CancellationToken ct = default)
    {
        var claim = await db.Claims.Include(c => c.Lines).FirstOrDefaultAsync(c => c.ClaimId == claimId && c.TenantId == tenantId, ct);
        if (claim is null) return new AppealResult(AppealOutcome.NotFound, null, null);
        if (!Appealable.Contains(claim.Status)) return new AppealResult(AppealOutcome.NotAppealable, null, claim);

        // A settled batch is never reopened — the appeal is recorded and routed to adjustment.
        var settled = false;
        if (claim.BatchId is { } batchId)
        {
            var batch = await db.ClaimBatches.AsNoTracking().FirstOrDefaultAsync(b => b.BatchId == batchId, ct);
            settled = batch is not null && batch.Status is BatchStatus.SettlementIssued or BatchStatus.Closed;
        }

        var line = lineId is { } lid ? claim.Lines.FirstOrDefault(l => l.ClaimLineId == lid) : null;
        var originalDecision = line is null ? (Guid?)null : await db.ClaimDecisions.AsNoTracking()
            .Where(d => d.ClaimLineId == line.ClaimLineId && !d.PendingSecondApproval)
            .OrderByDescending(d => d.DecidedAt).Select(d => (Guid?)d.DecisionId).FirstOrDefaultAsync(ct);

        var appeal = new ClaimAppeal
        {
            AppealId = Guid.NewGuid(), ClaimId = claimId, ClaimLineId = lineId, TenantId = tenantId,
            AppellantType = appellant, Reason = reason, ActingFor = actingFor, OriginalDecisionId = originalDecision,
            Resolution = settled ? AppealResolution.RoutedToAdjustment : AppealResolution.ReAdjudication,
            CreatedBy = actor, CreatedAt = clock.GetUtcNow(),
        };
        db.ClaimAppeals.Add(appeal);

        if (!settled)
        {
            // Re-enter adjudication; the prior decision rows are untouched (append-only). Return appealed lines to
            // Pending so the officer worklist surfaces them for a fresh decision by a DIFFERENT reviewer.
            claim.Status = ClaimStatus.UnderAdjudication;
            claim.DecidedAt = null;
            if (line is not null) line.Status = ClaimLineStatus.Pending;
            else foreach (var l in claim.Lines.Where(l => l.Status != ClaimLineStatus.Void)) l.Status = ClaimLineStatus.Pending;
        }
        await db.SaveChangesAsync(ct);
        return new AppealResult(settled ? AppealOutcome.RoutedToAdjustment : AppealOutcome.Raised, appeal, claim);
    }
}
