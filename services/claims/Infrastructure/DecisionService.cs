using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

public enum DecisionOutcome
{
    Recorded, PendingSecondApproval, Confirmed, Replayed, NotFound,
    SoDOriginator, SoDProviderAffiliated, SoDSameDecider, DualControlNotPending, Conflict, Validation,
}

public sealed record DecisionRequest(
    ClaimDecisionKind Kind, decimal? AllowedAmount, IReadOnlyList<string> ReasonCodes, string? Rationale,
    bool IsOverride, Guid? ConfirmsDecisionId);

public sealed record DecisionResult(
    DecisionOutcome Outcome, ClaimDecision? Decision, Claim? Claim, ClaimLine? Line,
    string? ValidationError = null, bool ClaimTerminal = false);

/// <summary>Line-level Claims Officer decisions (10b.4). Every decision is an APPEND-ONLY <c>claim_decision</c> row.
/// Enforced HERE (not in the UI): SoD (decider ≠ originator, not provider-affiliated), dual control above a value
/// threshold (a second distinct approver), mandatory reason code + rationale on deny/adjust/override, allowed-amount
/// bounds on partial. Line updates use optimistic concurrency (xmin) so two officers deciding the same line yield one
/// winner + one 409. Line decisions roll up to the claim status and (when batched) to the batch rollups, in one tx.</summary>
public sealed class DecisionService(ClaimsDbContext db, BatchRollupService rollups, TimeProvider clock)
{
    public async Task<DecisionResult> DecideAsync(
        string tenantId, string actor, string? callerProviderId, Guid claimId, Guid lineId,
        DecisionRequest req, string? idempotencyKey, decimal dualControlThreshold, string correlationId,
        CancellationToken ct = default)
    {
        var claim = await db.Claims.Include(c => c.Lines).FirstOrDefaultAsync(c => c.ClaimId == claimId && c.TenantId == tenantId, ct);
        var line = claim?.Lines.FirstOrDefault(l => l.ClaimLineId == lineId);
        if (claim is null || line is null) return Fail(DecisionOutcome.NotFound);

        // Idempotent replay.
        if (idempotencyKey is not null)
        {
            var prior = await db.ClaimDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.IdempotencyKey == idempotencyKey, ct);
            if (prior is not null) return new DecisionResult(DecisionOutcome.Replayed, prior, claim, line);
        }

        // Segregation of duties — enforced at the service.
        if (string.Equals(actor, claim.CreatedBy, StringComparison.Ordinal))
            return Fail(DecisionOutcome.SoDOriginator);
        if (callerProviderId is not null && claim.ProviderId?.ToString() == callerProviderId)
            return Fail(DecisionOutcome.SoDProviderAffiliated);

        return req.ConfirmsDecisionId is { } confirmId
            ? await ConfirmAsync(tenantId, actor, claim, line, confirmId, idempotencyKey, correlationId, ct)
            : await NewDecisionAsync(tenantId, actor, claim, line, req, idempotencyKey, dualControlThreshold, correlationId, ct);
    }

    private async Task<DecisionResult> NewDecisionAsync(
        string tenantId, string actor, Claim claim, ClaimLine line, DecisionRequest req,
        string? idempotencyKey, decimal threshold, string correlationId, CancellationToken ct)
    {
        // SoD on re-decision (10b.9 appeals): a person may not decide the SAME line twice — an appealed line must be
        // escalated to a DIFFERENT reviewer. A prior terminal (non-pending) decision by this actor ⇒ 403.
        if (await db.ClaimDecisions.AsNoTracking()
            .AnyAsync(d => d.ClaimLineId == line.ClaimLineId && d.DecidedBy == actor && !d.PendingSecondApproval, ct))
            return Fail(DecisionOutcome.SoDSameDecider);

        var err = DecisionRules.Validate(req.Kind, req.AllowedAmount, req.ReasonCodes, req.Rationale,
            line.BilledAmount, line.ContractPrice, req.IsOverride);
        if (err is not null) return new DecisionResult(DecisionOutcome.Validation, null, claim, line, err);

        var value = req.AllowedAmount ?? line.BilledAmount;
        var pending = value > threshold;

        var decision = NewRow(tenantId, claim, line, req, actor, correlationId, idempotencyKey, pending, null);
        db.ClaimDecisions.Add(decision);

        if (pending)
        {
            // Dual control: recorded, but the line is NOT changed until a second distinct approver confirms.
            return await SaveAsync(DecisionOutcome.PendingSecondApproval, decision, claim, line, false, ct);
        }

        ApplyEffect(claim, line, req.Kind, req.AllowedAmount);
        var terminal = await RecomputeClaimAsync(claim, line, ct);
        await rollups.RecomputeForClaimAsync(claim, ct);
        return await SaveAsync(DecisionOutcome.Recorded, decision, claim, line, terminal, ct);
    }

    private async Task<DecisionResult> ConfirmAsync(
        string tenantId, string actor, Claim claim, ClaimLine line, Guid confirmId,
        string? idempotencyKey, string correlationId, CancellationToken ct)
    {
        var pending = await db.ClaimDecisions.FirstOrDefaultAsync(
            d => d.DecisionId == confirmId && d.ClaimLineId == line.ClaimLineId && d.PendingSecondApproval, ct);
        if (pending is null) return Fail(DecisionOutcome.DualControlNotPending);
        // The confirmer must be a DIFFERENT person than the first approver.
        if (string.Equals(pending.DecidedBy, actor, StringComparison.Ordinal))
            return Fail(DecisionOutcome.SoDSameDecider);

        var req = new DecisionRequest(pending.Decision, pending.AllowedAmount, pending.ReasonCodes, pending.Rationale, true, null);
        var confirming = NewRow(tenantId, claim, line, req, actor, correlationId, idempotencyKey, false, pending.DecisionId);
        db.ClaimDecisions.Add(confirming);

        ApplyEffect(claim, line, pending.Decision, pending.AllowedAmount);
        var terminal = await RecomputeClaimAsync(claim, line, ct);
        await rollups.RecomputeForClaimAsync(claim, ct);
        return await SaveAsync(DecisionOutcome.Confirmed, confirming, claim, line, terminal, ct);
    }

    // ---- effect + rollups ---------------------------------------------------------------------------------
    private static void ApplyEffect(Claim claim, ClaimLine line, ClaimDecisionKind kind, decimal? allowed)
    {
        var effect = DecisionRules.Apply(kind, allowed, line.BilledAmount, line.ContractPrice);
        if (effect is { } e) { line.Status = e.Status; line.AllowedAmount = e.Allowed; }
        else claim.Status = kind == ClaimDecisionKind.RequestInfo ? ClaimStatus.PendingInfo : ClaimStatus.ClinicalReview;
    }

    private async Task<bool> RecomputeClaimAsync(Claim claim, ClaimLine line, CancellationToken ct)
    {
        // RequestInfo/RouteToClinical set the claim status directly (line stays Pending); do not roll up over them.
        if (line.Status == ClaimLineStatus.Pending) return false;

        var status = DecisionRules.RollUp(claim.Lines.Select(l => l.Status).ToList());
        claim.Status = status;
        var terminal = status is ClaimStatus.Approved or ClaimStatus.PartiallyApproved or ClaimStatus.Denied;
        if (terminal) claim.DecidedAt = clock.GetUtcNow();
        // 18.A2: claim totals go through the SAME canonical component split as the batch, so an
        // adjustment is never erased by a later decision on a sibling line.
        await rollups.RecomputeClaimTotalsAsync(claim, ct);
        return terminal;
    }

    // ---- persistence --------------------------------------------------------------------------------------
    private async Task<DecisionResult> SaveAsync(DecisionOutcome outcome,
        ClaimDecision decision, Claim claim, ClaimLine line, bool terminal, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new DecisionResult(outcome, decision, claim, line, null, terminal);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return Fail(DecisionOutcome.Conflict);   // another officer decided this line first
        }
        catch (DbUpdateException ex) when (IsUnique(ex, "ux_decision_idempotency"))
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            var prior = await db.ClaimDecisions.AsNoTracking().FirstAsync(d => d.IdempotencyKey == decision.IdempotencyKey, ct);
            return new DecisionResult(DecisionOutcome.Replayed, prior, claim, line);
        }
    }

    private ClaimDecision NewRow(string tenantId, Claim claim, ClaimLine line, DecisionRequest req, string actor,
        string correlationId, string? idempotencyKey, bool pending, Guid? confirms) => new()
    {
        DecisionId = Guid.NewGuid(), ClaimLineId = line.ClaimLineId, ClaimId = claim.ClaimId, TenantId = tenantId,
        Decision = req.Kind, AllowedAmount = req.AllowedAmount, ReasonCodes = [.. req.ReasonCodes], Rationale = req.Rationale,
        DecidedBy = actor, DecidedAt = clock.GetUtcNow(), RuleVersion = Adjudicator.RuleVersion,
        CorrelationId = correlationId, PendingSecondApproval = pending, ConfirmsDecisionId = confirms,
        IdempotencyKey = idempotencyKey,
    };

    private static DecisionResult Fail(DecisionOutcome o) => new(o, null, null, null);

    private static bool IsUnique(DbUpdateException ex, string constraint)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return (e.GetType().GetProperty("ConstraintName")?.GetValue(e) as string) == constraint;
        return false;
    }
}
