using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Api;

/// <summary>Phase 10b.4 — the Claims Officer worklist + line-level decisions. The worklist is a min-necessary,
/// clinical-free projection (codes + amounts + recommendation + document/result EXISTENCE, never values); every read
/// is audited. Decisions are append-only, SoD-checked (decider ≠ originator, not provider-affiliated), dual-controlled
/// above a value threshold, and require a reason code + rationale on deny/adjust/override. They roll up to the claim
/// status and the batch rollups. <c>Idempotency-Key</c> is required on a decision.</summary>
public static class DecisionEndpoints
{
    public static void MapDecisions(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/claims");

        // --- worklist --------------------------------------------------------------------------------------
        v1.MapGet("/worklist", async (ClaimsDeps deps, CancellationToken ct,
            Guid? batchId, Guid? providerId, string? recommendation, string? reasonCode,
            decimal? minValue, decimal? maxValue, int? take) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Review, ct);
            if (denied is not null) return denied;

            var effProvider = deps.ProviderId is { } pid && Guid.TryParse(pid, out var pg) ? pg : providerId;
            var q = deps.Db.Claims.AsNoTracking().Include(c => c.Lines)
                .Where(c => c.TenantId == deps.Tenant && c.Status == ClaimStatus.UnderAdjudication);
            if (effProvider is not null) q = q.Where(c => c.ProviderId == effProvider);
            if (batchId is not null) q = q.Where(c => c.BatchId == batchId);

            var claims = await q.OrderBy(c => c.CreatedAt).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct);
            var rows = claims.SelectMany(c => c.Lines
                .Where(l => l.Status == ClaimLineStatus.Pending)
                .Where(l => recommendation is null || l.SystemRecommendation?.ToString() == recommendation)
                .Where(l => reasonCode is null || l.ReasonCodes.Contains(reasonCode))
                .Where(l => minValue is null || l.BilledAmount >= minValue)
                .Where(l => maxValue is null || l.BilledAmount <= maxValue)
                .Select(l => WorklistRow.From(c, l)))
                .ToList();

            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "claim_worklist", EntityId = $"count={rows.Count}", Action = AuditAction.Read,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                FieldClasses = ["financials"],
            }, ct);
            return Results.Ok(rows);
        }).RequireAuthorization(HbmpPolicies.Scope("claims:review"));

        // --- line decision ---------------------------------------------------------------------------------
        v1.MapPost("/{claimId:guid}/lines/{lineId:guid}/decisions", async (
            Guid claimId, Guid lineId, DecisionBody body, HttpRequest http, ClaimsDeps deps,
            DecisionService decisions, ClaimsOptions opts, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Decide, ct);
            if (denied is not null) return denied;

            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "idempotency-key-required",
                    detail: "The Idempotency-Key header is required on a decision.", type: "urn:hbmp:idempotency-required");

            if (!Enum.TryParse<ClaimDecisionKind>(body.Decision, true, out var kind))
                return Results.Problem(statusCode: 422, title: "bad-decision", detail: "Unknown decision.", type: "urn:hbmp:validation");

            var req = new DecisionRequest(kind, body.AllowedAmount, body.ReasonCodes ?? [], body.Rationale,
                body.Override ?? false, body.ConfirmsDecisionId);
            var correlation = http.HttpContext?.TraceIdentifier ?? "";
            // 24.x — the events are enqueued INSIDE the decision's transaction rather than after it
            // returns. A claim line decided is money: a crash between the two commits left the line
            // settled with nothing downstream ever told, so the batch rollup, the settlement advice and
            // the notification all describe a claim that no longer exists in that state.
            var r = await decisions.DecideAsync(deps.Tenant, deps.Subject ?? "unknown", deps.ProviderId,
                claimId, lineId, req, idem, opts.DualControlThreshold, correlation,
                insideTransaction: async (claim, line, decision, terminal, c) =>
                {
                    await deps.Outbox.EnqueueAsync("ClaimLineDecided.v1", "claims.events",
                        new { claimId, line.ClaimLineId, decision = kind.ToString(), decision.AllowedAmount, tenantId = deps.Tenant }, c);
                    if (terminal)
                        await deps.Outbox.EnqueueAsync($"Claim{claim.Status}.v1", "claims.events",
                            new { claimId, status = claim.Status.ToString(), claim.NetPayable, tenantId = deps.Tenant }, c);
                },
                ct);

            switch (r.Outcome)
            {
                case DecisionOutcome.NotFound: return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
                case DecisionOutcome.SoDOriginator: return await SoD(deps, claimId, "SOD_ORIGINATOR_CANNOT_ADJUDICATE", "You created this claim.");
                case DecisionOutcome.SoDProviderAffiliated: return await SoD(deps, claimId, "SOD_PROVIDER_AFFILIATED", "You are affiliated with the claiming provider.");
                case DecisionOutcome.SoDSameDecider: return await SoD(deps, claimId, "SOD_SAME_DECIDER", "A second, distinct approver is required.");
                case DecisionOutcome.DualControlNotPending: return Conflict("no pending decision to confirm.");
                case DecisionOutcome.Conflict: return Conflict("this line was decided concurrently.");
                case DecisionOutcome.Validation:
                    return Results.Problem(statusCode: 422, title: r.ValidationError, type: "urn:hbmp:validation",
                        detail: "The decision is missing a mandatory field or is out of bounds.");
                case DecisionOutcome.Replayed:
                    return Results.Ok(new { outcome = "Replayed", decisionId = r.Decision!.DecisionId });
                case DecisionOutcome.PendingSecondApproval:
                    await Audit(deps, claimId, r.Line!.ClaimLineId, "DecisionPendingSecondApproval", AuditSeverity.Notice);
                    return Results.Accepted($"/api/v1/claims/{claimId}", new
                    {
                        outcome = "PendingSecondApproval", decisionId = r.Decision!.DecisionId,
                        message = "This decision exceeds the dual-control threshold and needs a second distinct approver.",
                    });
                default: // Recorded / Confirmed — the events already committed with the decision above.
                    await Audit(deps, claimId, r.Line!.ClaimLineId, $"ClaimLineDecided:{kind}", AuditSeverity.Notice);
                    return Results.Ok(new
                    {
                        outcome = r.Outcome.ToString(), decisionId = r.Decision!.DecisionId,
                        lineStatus = r.Line.Status.ToString(), claimStatus = r.Claim!.Status.ToString(),
                        allowedAmount = r.Line.AllowedAmount,
                    });
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:decide"));
    }

    private static async Task<IResult> SoD(ClaimsDeps deps, Guid claimId, string reason, string detail)
    {
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "claim", EntityId = claimId.ToString(), Action = AuditAction.Decision,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
            DecisionOutcome = "Deny", DecisionReasonCode = reason, Severity = AuditSeverity.High,
        });
        return Results.Problem(statusCode: 403, title: "segregation-of-duties", type: "urn:hbmp:sod-violation",
            detail: detail, extensions: new Dictionary<string, object?> { ["reason"] = reason });
    }

    private static async Task Audit(ClaimsDeps deps, Guid claimId, Guid lineId, string outcome, AuditSeverity severity) =>
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "claim_decision", EntityId = lineId.ToString(), Action = AuditAction.Decision,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
            DecisionOutcome = outcome, Severity = severity, FieldClasses = ["financials"],
        });

    private static IResult Conflict(string detail) =>
        Results.Problem(statusCode: 409, title: "conflict", detail: detail, type: "urn:hbmp:conflict");
}

/// <summary>Decision request body. <c>Override</c> flags a supervisor override (makes rationale mandatory);
/// <c>ConfirmsDecisionId</c> is the second-approver confirmation of a pending dual-control decision.</summary>
public sealed record DecisionBody(
    string Decision, decimal? AllowedAmount, IReadOnlyList<string>? ReasonCodes, string? Rationale,
    bool? Override, Guid? ConfirmsDecisionId);

/// <summary>Min-necessary worklist row — codes, amounts, adjudication output, linkage, and result EXISTENCE only.
/// No diagnosis / EMR note / result value: <c>ResultExists</c> is a boolean derived from the fulfillment linkage so
/// the officer can verify the service was rendered WITHOUT reading it.</summary>
public sealed record WorklistRow(
    Guid ClaimId, string ClaimNo, Guid ClaimLineId, Guid? ProviderId, Guid? ProviderLocationId,
    DateOnly ServiceDate, string CodeSystem, string Code, string? Description, decimal Quantity,
    decimal BilledAmount, decimal? ContractPrice, decimal? AllowedAmount, string Status,
    string? SystemRecommendation, IReadOnlyList<string> ReasonCodes, Guid? AuthorizationId,
    Guid? FulfillmentRef, bool ResultExists)
{
    public static WorklistRow From(Claim c, ClaimLine l) => new(
        c.ClaimId, c.ClaimNo, l.ClaimLineId, c.ProviderId, c.ProviderLocationId, c.ServiceDateFrom,
        l.CodeSystem.ToString(), l.Code, l.Description, l.Quantity, l.BilledAmount, l.ContractPrice, l.AllowedAmount,
        l.Status.ToString(), l.SystemRecommendation?.ToString(), l.ReasonCodes, l.AuthorizationId,
        l.FulfillmentRef, l.FulfillmentRef is not null);
}
