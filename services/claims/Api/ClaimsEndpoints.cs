using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;

namespace Mersal.Claims.Api;

/// <summary>Phase 10b.1 — the auto-derived origination channel + min-necessary claim reads. Every read is a
/// clinical-free <see cref="ClaimView"/> (codes + amounts only). Provider users are isolated to their own claims
/// (their principal's provider id is forced onto the filter); Mersal staff read tenant-wide. The intake seam creates
/// exactly one payable line per fulfillment reference — a second is denied DUPLICATE_CLAIM (409) at the database.</summary>
public static class ClaimsEndpoints
{
    public static void MapClaims(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/claims");

        // --- Reads (min-necessary projection) --------------------------------------------------------------
        v1.MapGet("", async (ClaimsDeps deps, CancellationToken ct,
            Guid? providerId, Guid? beneficiaryId, string? status, int? take) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.ReadClaim, ct);
            if (denied is not null) return denied;

            // Provider isolation: a provider-scoped caller can only ever see its own claims.
            var effectiveProvider = ProviderFilter(deps, providerId);
            ClaimStatus? st = Enum.TryParse<ClaimStatus>(status, true, out var s) ? s : null;
            var rows = await deps.Queries.ListAsync(deps.Tenant, effectiveProvider, beneficiaryId, st, take ?? 50, ct);
            await AuditRead(deps, "claim_list", $"count={rows.Count}");
            return Results.Ok(rows.Select(ClaimView.From).ToList());
        }).RequireAuthorization(HbmpPolicies.Scope("claims:read"));

        v1.MapGet("/{id:guid}", async (Guid id, ClaimsDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.ReadClaim, ct);
            if (denied is not null) return denied;

            var claim = await deps.Queries.GetAsync(deps.Tenant, id, ct);
            if (claim is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            // Provider isolation defence-in-depth: a provider may not read another provider's claim.
            if (deps.ProviderId is { } pid && Guid.TryParse(pid, out var pg) && claim.ProviderId != pg)
                return Results.Problem(statusCode: 403, title: "access-denied", type: "urn:hbmp:claims-access-denied",
                    detail: "You are not permitted to read this claim.");
            await AuditRead(deps, "claim", claim.ClaimNo);
            return Results.Ok(ClaimView.From(claim));
        }).RequireAuthorization(HbmpPolicies.Scope("claims:read"));

        // --- Pre-adjudication (10b.3) ----------------------------------------------------------------------
        v1.MapPost("/{id:guid}/adjudicate", async (Guid id, HttpRequest http, ClaimsDeps deps,
            AdjudicationService adjudicator, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Adjudicate, ct);
            if (denied is not null) return denied;

            var results = await adjudicator.AdjudicateAsync(deps.Tenant, id, http.Headers.Authorization.ToString(), ct);
            if (results is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            await deps.Outbox.EnqueueAsync("ClaimAdjudicated.v1", "claims.events",
                new { claimId = id, lines = results.Count, ruleVersion = Domain.Adjudicator.RuleVersion, tenantId = deps.Tenant }, ct);
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "claim", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                DecisionOutcome = "ClaimAdjudicated", DecisionPolicyId = Domain.Adjudicator.RuleVersion,
                AfterState = "UnderAdjudication", FieldClasses = ["financials"],
            }, ct);
            return Results.Ok(results.Select(r => new
            {
                r.ClaimLineId,
                recommendation = r.Result.Recommendation.ToString(),
                reasonCodes = r.Result.ReasonCodes,
                allowedAmount = r.Result.AllowedAmount,
                r.Result.MemberShare,
                ruleVersion = r.Result.RuleVersion,
            }).ToList());
        }).RequireAuthorization(HbmpPolicies.Scope("claims:adjudicate"));

        // --- Auto-derive intake seam (system) --------------------------------------------------------------
        // Mirrors finance /projections: pending the fanout bus, delivery events are ingested through this endpoint.
        v1.MapPost("/intake", async (ClaimIntakeRequest req, HttpRequest http, ClaimsDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Ingest, ct);
            if (denied is not null) return denied;

            var ev = new ClaimIntakeEvent(
                req.EventId, req.EventType, req.TenantId, req.FulfillmentRef, req.FulfillmentType,
                req.BeneficiaryId, req.ProviderId, req.ProviderLocationId, req.AuthorizationId,
                req.CodeSystem, req.Code, req.Description, req.Quantity, req.BilledAmount,
                req.ServiceDate, string.IsNullOrWhiteSpace(req.CurrencyCode) ? "EGP" : req.CurrencyCode, req.OccurredAt);

            var bearer = http.Headers.Authorization.ToString();
            var result = await deps.Intake.IngestAsync(ev, bearer,
                insideTransaction: async (claim, line, newClaim, c) =>
                {
                    // Publish via the transactional outbox in the SAME transaction as the insert (16 §7).
                    if (newClaim)
                        await deps.Outbox.EnqueueAsync("ClaimCreated.v1", "claims.events",
                            new { claimId = claim.ClaimId, claim.ClaimNo, origin = claim.Origin.ToString(), tenantId = claim.TenantId }, c);
                    await deps.Outbox.EnqueueAsync("ClaimLineCreated.v1", "claims.events",
                        new { claimId = claim.ClaimId, claimLineId = line.ClaimLineId, line.Code, line.FulfillmentRef }, c);
                }, ct);

            switch (result.Outcome)
            {
                case IntakeOutcome.Duplicate:
                    return Results.Problem(statusCode: 409, title: "duplicate-claim", type: "urn:hbmp:duplicate-claim",
                        detail: "A live payable claim line already exists for this fulfillment reference.",
                        extensions: new Dictionary<string, object?> { ["reason"] = ReasonCodes.DuplicateClaim });
                case IntakeOutcome.Replayed:
                    return Results.Ok(new { outcome = "Replayed", claimId = result.Claim?.ClaimId, claimLineId = result.Line?.ClaimLineId });
                default:
                    await deps.Audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "claim_line", EntityId = result.Line!.ClaimLineId.ToString(), Action = AuditAction.Create,
                        ActorUserId = deps.Subject ?? "claims-service", ActorRole = deps.Roles, TenantId = result.Claim!.TenantId,
                        ProviderId = result.Claim.ProviderId?.ToString(), AfterState = result.Claim.Status.ToString(),
                        DecisionOutcome = "ClaimLineCreated", FieldClasses = ["financials"],
                    }, ct);
                    return Results.Ok(new { outcome = "Created", claimId = result.Claim!.ClaimId, claimLineId = result.Line!.ClaimLineId });
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:ingest"));
    }

    /// <summary>Force a provider-scoped caller onto its own provider id; Mersal staff may pass an optional filter.</summary>
    private static Guid? ProviderFilter(ClaimsDeps deps, Guid? requested)
    {
        if (deps.ProviderId is { } pid && Guid.TryParse(pid, out var pg)) return pg;
        return requested;
    }

    private static async Task AuditRead(ClaimsDeps deps, string entityType, string entityId) =>
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = entityType, EntityId = entityId, Action = AuditAction.Read,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
            ProviderId = deps.ProviderId, FieldClasses = ["financials"],
        });
}
