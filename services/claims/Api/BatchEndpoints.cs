using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Api;

/// <summary>Phase 10b.2 — batching + batch lifecycle. A batch is the unit of review and settlement for one payee.
/// The single-open-batch guarantee is the DB index <c>ux_claim_one_open_batch</c> (a claim can never sit in two live
/// batches → it can never be settled twice); a violation is CLAIM_ALREADY_BATCHED (409). Decided is blocked while any
/// line is undecided (422). Rollups recompute on every change and freeze at SettlementIssued. Every membership change
/// and transition is audited; BatchCreated / BatchUnderReview / BatchDecided publish via the outbox.</summary>
public static class BatchEndpoints
{
    public static void MapBatches(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/claim-batches");

        // --- create ----------------------------------------------------------------------------------------
        v1.MapPost("", async (CreateBatchRequest req, ClaimsDeps deps, BatchService batches, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Batch, ct);
            if (denied is not null) return denied;
            if (req.PeriodTo < req.PeriodFrom) return Unprocessable("bad-period", "period_to precedes period_from.");

            var sel = new BatchSelector(req.BatchType, req.SelectionMode, req.PayeeProviderId, req.ProviderLocationId,
                req.ProviderGroupId, req.PeriodFrom, req.PeriodTo, req.ServiceDateFrom, req.ServiceDateTo, req.ClaimIds);
            // 24.x — a batch is the unit money is paid in. Created here and unannounced downstream, it is
            // a payment run nothing else knows to expect.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            var r = await batches.CreateAsync(deps.Tenant, deps.Subject, sel, ct);
            if (r.Outcome != BatchOutcome.Ok) return Map(r);

            await deps.Outbox.EnqueueAsync("BatchCreated.v1", "claims.events",
                new { batchId = r.Batch!.BatchId, r.Batch.BatchNo, mode = r.Batch.SelectionMode.ToString(), tenantId = deps.Tenant }, ct);
            await Audit(deps, AuditAction.Create, r.Batch.BatchId.ToString(), "BatchCreated", null, r.Batch.Status.ToString());
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/claim-batches/{r.Batch.BatchId}", BatchView.From(r.Batch));
        }).RequireAuthorization(HbmpPolicies.Scope("claims:batch"));

        // --- membership ------------------------------------------------------------------------------------
        v1.MapPost("/{id:guid}/claims", async (Guid id, AddClaimBody body, ClaimsDeps deps, BatchService batches, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Batch, ct);
            if (denied is not null) return denied;
            var r = await batches.AddClaimAsync(deps.Tenant, deps.Subject, id, body.ClaimId, ct);
            if (r.Outcome != BatchOutcome.Ok) return Map(r);
            await Audit(deps, AuditAction.Update, id.ToString(), "BatchClaimAdded", null, body.ClaimId.ToString());
            return Results.Ok(BatchView.From(r.Batch!));
        }).RequireAuthorization(HbmpPolicies.Scope("claims:batch"));

        v1.MapDelete("/{id:guid}/claims/{claimId:guid}", async (Guid id, Guid claimId, string? reason,
            ClaimsDeps deps, BatchService batches, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Batch, ct);
            if (denied is not null) return denied;
            var r = await batches.RemoveClaimAsync(deps.Tenant, deps.Subject, id, claimId, reason, ct);
            if (r.Outcome != BatchOutcome.Ok) return Map(r);
            // Removal from an UnderReview batch is an audited EXCEPTION (reason mandatory, enforced above).
            var severity = r.Batch!.Status == BatchStatus.UnderReview ? AuditSeverity.Notice : AuditSeverity.Info;
            await Audit(deps, AuditAction.Update, id.ToString(), "BatchClaimRemoved", claimId.ToString(), reason, severity);
            return Results.Ok(BatchView.From(r.Batch));
        }).RequireAuthorization(HbmpPolicies.Scope("claims:batch"));

        // --- lifecycle -------------------------------------------------------------------------------------
        v1.MapPost("/{id:guid}/submit-for-review", (Guid id, ClaimsDeps deps, BatchService b, CancellationToken ct) =>
            TransitionEndpoint(id, BatchStatus.UnderReview, null, "BatchUnderReview.v1", "BatchUnderReview", deps, b, ct))
            .RequireAuthorization(HbmpPolicies.Scope("claims:batch"));

        v1.MapPost("/{id:guid}/decide", (Guid id, ClaimsDeps deps, BatchService b, CancellationToken ct) =>
            TransitionEndpoint(id, BatchStatus.Decided, null, "BatchDecided.v1", "BatchDecided", deps, b, ct))
            .RequireAuthorization(HbmpPolicies.Scope("claims:batch"));

        v1.MapPost("/{id:guid}/reopen", (Guid id, ClaimsDeps deps, BatchService b, CancellationToken ct) =>
            TransitionEndpoint(id, BatchStatus.Open, null, null, "BatchReopened", deps, b, ct))
            .RequireAuthorization(HbmpPolicies.Scope("claims:batch"));

        v1.MapPost("/{id:guid}/cancel", async (Guid id, CancelBatchRequest? body, ClaimsDeps deps, BatchService b, CancellationToken ct) =>
            await TransitionEndpoint(id, BatchStatus.Cancelled, body?.Reason, null, "BatchCancelled", deps, b, ct))
            .RequireAuthorization(HbmpPolicies.Scope("claims:batch"));

        // --- reads -----------------------------------------------------------------------------------------
        v1.MapGet("", async (ClaimsDeps deps, CancellationToken ct, string? status) =>
        {
            // §3.4: claim_batch R🔒🟠PO — a payee reads its OWN batches. The payee predicate below is what
            // makes that true; before the provider read existed it could not be reached at all.
            var denied = await deps.Gate.CheckClaimReadAsync(ct);
            if (denied is not null) return denied;
            var q = deps.Db.ClaimBatches.AsNoTracking().Include(b => b.Items).Where(b => b.TenantId == deps.Tenant);
            if (deps.ProviderId is { } pid && Guid.TryParse(pid, out var pg)) q = q.Where(b => b.PayeeProviderId == pg);
            if (Enum.TryParse<BatchStatus>(status, true, out var st)) q = q.Where(b => b.Status == st);
            var rows = await q.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(BatchView.From).ToList());
        }).RequireAuthorization(HbmpPolicies.Scope("claims:read"));

        v1.MapGet("/{id:guid}", async (Guid id, ClaimsDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckClaimReadAsync(ct);
            if (denied is not null) return denied;
            var b = await deps.Db.ClaimBatches.AsNoTracking().Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.BatchId == id && x.TenantId == deps.Tenant, ct);
            if (b is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            // Ownership re-evaluated against the PAYEE now that the row is in hand — a reimbursement batch has
            // no payee provider, and no provider is a party to one.
            var crossProvider = await deps.Gate.CheckClaimReadAsync(ct, new ClaimRow(b.PayeeProviderId));
            if (crossProvider is not null) return crossProvider;
            if (deps.ProviderId is { } pid && Guid.TryParse(pid, out var pg) && b.PayeeProviderId != pg)
                return Results.Problem(statusCode: 403, title: "access-denied", type: "urn:hbmp:claims-access-denied");
            return Results.Ok(BatchView.From(b));
        }).RequireAuthorization(HbmpPolicies.Scope("claims:read"));
    }

    private static async Task<IResult> TransitionEndpoint(Guid id, BatchStatus to, string? reason,
        string? eventType, string outcome, ClaimsDeps deps, BatchService batches, CancellationToken ct)
    {
        var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Batch, ct);
        if (denied is not null) return denied;
        var before = (await deps.Db.ClaimBatches.AsNoTracking().FirstOrDefaultAsync(b => b.BatchId == id && b.TenantId == deps.Tenant, ct))?.Status;
        // 24.x — a batch transition and the event announcing it are one fact or neither.
        await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
        var r = await batches.TransitionAsync(deps.Tenant, id, to, reason, ct);
        if (r.Outcome != BatchOutcome.Ok) return Map(r);
        if (eventType is not null)
            await deps.Outbox.EnqueueAsync(eventType, "claims.events",
                new { batchId = id, status = to.ToString(), netPayable = r.Batch!.NetPayable, tenantId = deps.Tenant }, ct);
        await Audit(deps, AuditAction.StateChange, id.ToString(), outcome, before?.ToString(), to.ToString());
        await tx.CommitAsync(ct);
        return Results.Ok(BatchView.From(r.Batch!));
    }

    private static IResult Map(BatchResult r) => r.Outcome switch
    {
        BatchOutcome.NotFound => Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"),
        BatchOutcome.IllegalTransition => Conflict("illegal-transition", "That batch transition is not allowed from the current status."),
        BatchOutcome.AlreadyBatched => Conflict("claim-already-batched", "This claim already sits in a live batch.", "CLAIM_ALREADY_BATCHED"),
        BatchOutcome.MembershipLocked => Conflict("membership-locked", "The batch is decided/settled/closed; membership is locked."),
        BatchOutcome.EmptyBatch => Unprocessable("empty-batch", "A batch needs at least one claim to enter review."),
        BatchOutcome.UndecidedLines => Results.Problem(statusCode: 422, title: "undecided-lines",
            type: "urn:hbmp:undecided-lines", detail: "Every line must be decided before the batch can be Decided.",
            extensions: new Dictionary<string, object?> { ["undecidedClaimLineIds"] = r.UndecidedClaimLines }),
        BatchOutcome.ProviderMismatch => Unprocessable("provider-mismatch", "A provider batch must be homogeneous (one payee)."),
        BatchOutcome.ReasonRequired => Unprocessable("reason-required", "A reason is mandatory for this action."),
        BatchOutcome.PayeeRequired => Unprocessable("payee-required", "A provider batch needs a payee provider."),
        _ => Results.Ok(),
    };

    private static async Task Audit(ClaimsDeps deps, AuditAction action, string entityId, string outcome,
        string? before, string? after, AuditSeverity severity = AuditSeverity.Info) =>
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "claim_batch", EntityId = entityId, Action = action,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
            BeforeState = before, AfterState = after, DecisionOutcome = outcome, Severity = severity,
            FieldClasses = ["financials"],
        });

    private static IResult Unprocessable(string title, string detail) =>
        Results.Problem(statusCode: 422, title: title, detail: detail, type: "urn:hbmp:validation");
    private static IResult Conflict(string title, string detail, string? reason = null) =>
        Results.Problem(statusCode: 409, title: title, detail: detail, type: "urn:hbmp:conflict",
            extensions: reason is null ? null : new Dictionary<string, object?> { ["reason"] = reason });
}

/// <summary>Body for adding a claim to a batch.</summary>
public sealed record AddClaimBody(Guid ClaimId);
