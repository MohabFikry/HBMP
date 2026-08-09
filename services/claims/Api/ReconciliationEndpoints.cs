using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Domain;
using Mersal.Time;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

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

        // --- read the adjustments on a claim (§3.4 claim_adjustment: R✅ staff, R🔒🟠PO provider) ------------
        //
        // 24.4 — there was no read at all, for anyone. An adjustment MOVES MONEY on a settled claim, and the
        // only way to see one was to have been the person who raised it (or to read the audit trail, which
        // providers cannot). The permission matrix has given the payee this read since 10b.7; nothing served it.
        app.MapGet("/api/v1/claims/{claimId:guid}/adjustments", async (
            Guid claimId, ClaimsDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckClaimReadAsync(ct);
            if (denied is not null) return denied;

            var claim = await deps.Db.Claims.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ClaimId == claimId && c.TenantId == deps.Tenant, ct);
            if (claim is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            // Ownership against the claim the adjustments hang off — a provider reads its own, and a staff
            // caller affiliated with a provider is held to theirs by the second check, as on the claim read.
            var crossProvider = await deps.Gate.CheckClaimReadAsync(ct, new ClaimRow(claim.ProviderId));
            if (crossProvider is not null) return crossProvider;
            if (deps.ProviderId is { } pid && Guid.TryParse(pid, out var pg) && claim.ProviderId != pg)
                return Results.Problem(statusCode: 403, title: "access-denied", type: "urn:hbmp:claims-access-denied",
                    detail: "You are not permitted to read this claim.");

            var rows = await deps.Db.ClaimAdjustments.AsNoTracking()
                .Where(a => a.ClaimId == claimId && a.TenantId == deps.Tenant)
                .OrderBy(a => a.AdjustedAt).ToListAsync(ct);

            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "claim_adjustment", EntityId = claimId.ToString(), Action = AuditAction.Read,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                FieldClasses = ["financials"],
            }, ct);

            // Two DTOs, not one with nulls: the provider projection is structurally incapable of carrying the
            // Mersal signatory or the internal rationale, which is the same argument the clinical-free claim
            // projection rests on.
            return deps.IsProviderCaller
                ? Results.Ok(rows.Select(ProviderAdjustmentView.From).ToList())
                : Results.Ok(rows.Select(AdjustmentView.From).ToList());
        }).RequireAuthorization(HbmpPolicies.Scope("claims:read"));

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
                case AdjustmentOutcome.IdempotencyKeyReuse:
                    return Results.Problem(statusCode: 422, title: "idempotency-key-reuse",
                        type: "urn:hbmp:idempotency-key-reuse",
                        detail: "That key was already used for a different adjustment. Answering it with the earlier one would report a correction that was never applied.");
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

/// <summary>The staff view of an adjustment: who signed it, why, and what it moved. Clinical-free like every
/// claims projection — an adjustment is codes and amounts.</summary>
public sealed record AdjustmentView(
    Guid AdjustmentId, Guid ClaimLineId, string AdjustmentType, decimal AmountDelta, string ReasonCode,
    string Rationale, Guid? RecoversClaimLineId, decimal BeforeAmount, decimal AfterAmount,
    string AdjustedBy, DateTimeOffset AdjustedAt, bool PendingSecondApproval)
{
    public static AdjustmentView From(ClaimAdjustment a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new(a.AdjustmentId, a.ClaimLineId, a.AdjustmentType.ToString(), a.AmountDelta, a.ReasonCode,
            a.Rationale, a.RecoversClaimLineId, a.BeforeAmount, a.AfterAmount, a.AdjustedBy, a.AdjustedAt,
            a.PendingSecondApproval);
    }
}

/// <summary>The PAYEE's view (§3.4 R🔒🟠PO): what was adjusted, by how much, under which reason code, and
/// when — everything an appeal is argued from. It cannot carry <c>AdjustedBy</c> (which Mersal user signed
/// it), the internal <c>Rationale</c>, the correlation/idempotency keys, or the dual-control state: those are
/// Mersal's record of its own decision, not a statement to the counterparty.</summary>
public sealed record ProviderAdjustmentView(
    Guid AdjustmentId, Guid ClaimLineId, string AdjustmentType, decimal AmountDelta, string ReasonCode,
    Guid? RecoversClaimLineId, decimal BeforeAmount, decimal AfterAmount, DateTimeOffset AdjustedAt)
{
    public static ProviderAdjustmentView From(ClaimAdjustment a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new(a.AdjustmentId, a.ClaimLineId, a.AdjustmentType.ToString(), a.AmountDelta, a.ReasonCode,
            a.RecoversClaimLineId, a.BeforeAmount, a.AfterAmount, a.AdjustedAt);
    }
}

/// <summary>Adjustment request body. <c>ConfirmsAdjustmentId</c> is the second-approver confirmation of a pending
/// negative-net adjustment; <c>RecoversClaimLineId</c> is mandatory for Recovery/Clawback.</summary>
public sealed record AdjustmentBody(
    string Type, decimal AmountDelta, string? ReasonCode, string? Rationale,
    Guid? RecoversClaimLineId, Guid? ConfirmsAdjustmentId);
