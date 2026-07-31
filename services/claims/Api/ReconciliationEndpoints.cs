using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Domain;
using Mersal.Time;
using Mersal.Claims.Infrastructure;

namespace Mersal.Claims.Api;

/// <summary>Phase 10b.7 — the reconciliation worklist + append-only adjustments. The worklist buckets every discrepancy
/// over a period (matched · billed-not-delivered · delivered-not-billed · price/quantity variance · duplicate) as a
/// min-necessary, clinical-free projection. Adjustments are append-only signed entries with a mandatory reason +
/// rationale; a Recovery/Clawback references the original line; a negative batch net requires a second approver. Every
/// adjustment records BEFORE/AFTER amounts and is audited; the original decision is never mutated.</summary>
public static class ReconciliationEndpoints
{
    public static void MapReconciliation(this IEndpointRouteBuilder app)
    {
        // --- reconciliation worklist -----------------------------------------------------------------------
        app.MapGet("/api/v1/reconciliation", async (
            ClaimsDeps deps, ReconciliationQueries recon, CancellationToken ct,
            DateOnly? from, DateOnly? to, Guid? providerId, string? bucket, decimal? minValue, int? take) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Reconcile, ct);
            if (denied is not null) return denied;

            var effProvider = deps.ProviderId is { } pid && Guid.TryParse(pid, out var pg) ? pg : providerId;
            // 18.A3: the default window is Cairo days, not UTC days — a report opened at 23:30 local
            // used to silently exclude the day the operator is actually looking at.
            var t = to ?? deps.Calendar.Today();
            var f = from ?? t.AddDays(-90);
            var rows = await recon.ListAsync(deps.Tenant, effProvider, f, t, bucket, minValue, take ?? 500, ct);

            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "reconciliation", EntityId = $"count={rows.Count}", Action = AuditAction.Read,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                FieldClasses = ["financials"],
            }, ct);
            return Results.Ok(rows);
        }).RequireAuthorization(HbmpPolicies.Scope("claims:reconcile"));

        // --- raise an adjustment ---------------------------------------------------------------------------
        app.MapPost("/api/v1/claims/{claimId:guid}/lines/{lineId:guid}/adjustments", async (
            Guid claimId, Guid lineId, AdjustmentBody body, HttpRequest http, ClaimsDeps deps,
            AdjustmentService adjustments, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Adjust, ct);
            if (denied is not null) return denied;

            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "idempotency-key-required",
                    detail: "The Idempotency-Key header is required on an adjustment.", type: "urn:hbmp:idempotency-required");
            if (!Enum.TryParse<AdjustmentType>(body.Type, true, out var type))
                return Results.Problem(statusCode: 422, title: "bad-adjustment-type", type: "urn:hbmp:validation", detail: "Unknown adjustment type.");

            var req = new AdjustmentRequest(type, body.AmountDelta, body.ReasonCode, body.Rationale,
                body.RecoversClaimLineId, body.ConfirmsAdjustmentId);
            var correlation = http.HttpContext?.TraceIdentifier ?? "";
            // 24.x — an adjustment MOVES MONEY on a settled claim. AdjustmentService.SaveAsync already
            // joins an ambient transaction (18.A4 opens one around the dual-control decision), so the
            // endpoint opens one here and the write plus its ClaimAdjusted/ClaimVoided event commit as
            // one fact. Without it a reversal could land with nothing downstream told the money moved.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            var r = await adjustments.RaiseAsync(deps.Tenant, deps.Subject ?? "unknown", claimId, lineId, req, idem, correlation, ct);

            switch (r.Outcome)
            {
                case AdjustmentOutcome.NotFound: return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
                case AdjustmentOutcome.Validation:
                    return Results.Problem(statusCode: 422, title: r.ValidationError, type: "urn:hbmp:validation",
                        detail: "The adjustment is missing a mandatory field or violates a rule.");
                case AdjustmentOutcome.SoDSameApprover:
                    return Results.Problem(statusCode: 403, title: "segregation-of-duties", type: "urn:hbmp:sod-violation",
                        detail: "A second, distinct approver is required.");
                case AdjustmentOutcome.DualControlNotPending:
                    return Results.Problem(statusCode: 409, title: "conflict", type: "urn:hbmp:conflict", detail: "No pending adjustment to confirm.");
                case AdjustmentOutcome.Conflict:
                    return Results.Problem(statusCode: 409, title: "conflict", type: "urn:hbmp:conflict", detail: "This line was adjusted concurrently.");
                case AdjustmentOutcome.Replayed:
                    return Results.Ok(new { outcome = "Replayed", adjustmentId = r.Adjustment!.AdjustmentId });
                case AdjustmentOutcome.PendingSecondApproval:
                    await AuditAdjustment(deps, r.Adjustment!, AuditSeverity.Notice);
                    return Results.Accepted($"/api/v1/claims/{claimId}", new
                    {
                        outcome = "PendingSecondApproval", adjustmentId = r.Adjustment!.AdjustmentId,
                        message = "This adjustment would make the batch net payable negative and needs a second distinct approver.",
                    });
                default: // Recorded / Confirmed
                    var eventType = type is AdjustmentType.Reversal or AdjustmentType.Void ? "ClaimVoided.v1" : "ClaimAdjusted.v1";
                    await deps.Outbox.EnqueueAsync(eventType, "claims.events", new
                    {
                        claimId, r.Line!.ClaimLineId, adjustmentId = r.Adjustment!.AdjustmentId,
                        type = type.ToString(), r.Adjustment.AmountDelta, tenantId = deps.Tenant,
                    }, ct);
                    await AuditAdjustment(deps, r.Adjustment, AuditSeverity.Notice);
                    await tx.CommitAsync(ct);
                    return Results.Ok(new
                    {
                        outcome = r.Outcome.ToString(), adjustmentId = r.Adjustment.AdjustmentId,
                        r.Adjustment.BeforeAmount, r.Adjustment.AfterAmount, lineStatus = r.Line.Status.ToString(),
                    });
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:adjust"));
    }

    // BEFORE/AFTER amounts are recorded on the immutable, hash-chained audit event.
    private static async Task AuditAdjustment(ClaimsDeps deps, ClaimAdjustment a, AuditSeverity severity) =>
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "claim_adjustment", EntityId = a.AdjustmentId.ToString(), Action = AuditAction.StateChange,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
            DecisionOutcome = a.AdjustmentType.ToString(), DecisionReasonCode = a.ReasonCode, Severity = severity,
            FieldClasses = ["financials"],
            BeforeState = $"{{\"payable\":{a.BeforeAmount}}}", AfterState = $"{{\"payable\":{a.AfterAmount}}}",
        });
}

/// <summary>Adjustment request body. <c>ConfirmsAdjustmentId</c> is the second-approver confirmation of a pending
/// negative-net adjustment; <c>RecoversClaimLineId</c> is mandatory for Recovery/Clawback.</summary>
public sealed record AdjustmentBody(
    string Type, decimal AmountDelta, string? ReasonCode, string? Rationale,
    Guid? RecoversClaimLineId, Guid? ConfirmsAdjustmentId);
