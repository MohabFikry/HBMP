using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;

namespace Mersal.Claims.Api;

/// <summary>Phase 10b.9 — appeals + the claims KPI read-model feed. An appeal re-enters a live decided claim into
/// UnderAdjudication while PRESERVING the original decision thread (append-only), and the re-decision may not be made by
/// the original decider (SoD, enforced in the decision handler). An appeal on a settled batch is routed to a
/// compensating adjustment in a later batch — never a reopen. KPIs are aggregate-only and clinical-free.</summary>
public static class AppealEndpoints
{
    public static void MapAppeals(this IEndpointRouteBuilder app)
    {
        // --- appeal a decided claim ------------------------------------------------------------------------
        app.MapPost("/api/v1/claims/{id:guid}/appeals", async (
            Guid id, AppealBody body, ClaimsDeps deps, AppealService appeals, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Appeal, ct);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(body.Reason))
                return Results.Problem(statusCode: 422, title: "reason-required", type: "urn:hbmp:validation", detail: "An appeal reason is mandatory.");
            if (!Enum.TryParse<AppellantType>(body.AppellantType, true, out var appellant))
                return Results.Problem(statusCode: 422, title: "bad-appellant", type: "urn:hbmp:validation", detail: "Unknown appellant type.");

            var actingFor = deps.ProviderId is null ? deps.Subject : null; // Mersal acting for the appellant
            var r = await appeals.RaiseAsync(deps.Tenant, deps.Subject ?? "unknown", id, body.ClaimLineId, appellant, body.Reason, actingFor, ct);
            switch (r.Outcome)
            {
                case AppealOutcome.NotFound: return Results.NotFound();
                case AppealOutcome.NotAppealable:
                    return Results.Problem(statusCode: 409, title: "not-appealable", type: "urn:hbmp:conflict",
                        detail: "Only a decided claim (Approved / PartiallyApproved / Denied) can be appealed.");
                default:
                    await deps.Outbox.EnqueueAsync("ClaimAppealed.v1", "claims.events", new
                    {
                        r.Appeal!.AppealId, claimId = id, resolution = r.Appeal.Resolution.ToString(),
                        r.Appeal.OriginalDecisionId, tenantId = deps.Tenant,
                    }, ct);
                    await deps.Audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "claim_appeal", EntityId = r.Appeal.AppealId.ToString(), Action = AuditAction.StateChange,
                        ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                        DecisionOutcome = r.Appeal.Resolution.ToString(), Severity = AuditSeverity.Notice, FieldClasses = ["financials"],
                    }, ct);
                    return Results.Created($"/api/v1/claims/{id}/appeals/{r.Appeal.AppealId}", new
                    {
                        r.Appeal.AppealId, resolution = r.Appeal.Resolution.ToString(),
                        claimStatus = r.Claim!.Status.ToString(), originalDecisionId = r.Appeal.OriginalDecisionId,
                        note = r.Outcome == AppealOutcome.RoutedToAdjustment
                            ? "Batch already settled — correct via an adjustment/recovery in a later batch; this batch is untouched."
                            : "Claim re-entered adjudication; the original decision thread is preserved.",
                    });
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:appeal"));

        // --- claims KPI aggregate (read-model feed; reporting-service consumes) -----------------------------
        app.MapGet("/api/v1/claims/kpis", async (
            ClaimsDeps deps, KpiQueries kpis, CancellationToken ct, DateOnly? from, DateOnly? to) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Reconcile, ct);
            if (denied is not null) return denied;

            var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
            var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var result = await kpis.ComputeAsync(deps.Tenant, f, t, ct);

            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "claims_kpi", EntityId = $"{f}..{t}", Action = AuditAction.Read,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, FieldClasses = ["financials"],
            }, ct);
            return Results.Ok(result);
        }).RequireAuthorization(HbmpPolicies.Scope("claims:reconcile"));
    }
}

/// <summary>Appeal request. <c>ClaimLineId</c> narrows the appeal to one line (else the whole claim re-enters).</summary>
public sealed record AppealBody(string AppellantType, Guid? ClaimLineId, string Reason);
