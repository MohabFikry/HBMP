using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Infrastructure;

namespace Mersal.Claims.Api;

/// <summary>Phase 10b.8 — settlement advice + exports. On a Decided batch, generates an IMMUTABLE settlement advice
/// (append-only row + content hash + WORM document), freezes the rollups, and moves the batch to SettlementIssued;
/// regeneration writes a NEW version. Exports (CSV/XLSX/PDF) carry ZERO clinical fields, are audited, and are
/// provider-isolated. Recording an external payment reference is a fact only — SoD-separated from decide.
/// <b>THE PLATFORM NEVER MOVES MONEY: there is no payout endpoint or payment-rail call anywhere in this service.</b></summary>
public static class SettlementEndpoints
{
    public static void MapSettlement(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/claim-batches");

        // --- generate settlement advice (Decided → SettlementIssued, or a new version) ---------------------
        v1.MapPost("/{id:guid}/settlement-advice", async (
            Guid id, HttpRequest http, ClaimsDeps deps, SettlementService settlement, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Export, ct);
            if (denied is not null) return denied;

            var bearer = BearerOf(http);
            var r = await settlement.GenerateAsync(deps.Tenant, id, deps.Subject ?? "unknown", bearer, ct);
            switch (r.Outcome)
            {
                case SettlementOutcome.NotFound: return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
                case SettlementOutcome.BatchNotDecided:
                    return Results.Problem(statusCode: 409, title: "batch-not-decided", type: "urn:hbmp:conflict",
                        detail: "A settlement advice can only be generated for a Decided batch.");
                case SettlementOutcome.SoDSameActor:
                    // 18.A4 — segregation of duties: release is the last human control before money moves
                    // on this document, so it may not be performed by whoever assembled the batch (36 §9).
                    await deps.Audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "claim_batch", EntityId = id.ToString(), Action = AuditAction.Decision,
                        ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                        DecisionOutcome = "TransitionDenied", DecisionReasonCode = "sod-releaser-is-batch-creator",
                        Severity = AuditSeverity.High, FieldClasses = ["financials"],
                    }, ct);
                    return Results.Problem(statusCode: 409, title: "sod-violation", type: "urn:hbmp:sod-violation",
                        detail: "The settlement must be released by someone other than the person who created the batch.");
                default:
                    await deps.Outbox.EnqueueAsync("SettlementAdviceIssued.v1", "claims.events", new
                    {
                        adviceId = r.Advice!.AdviceId, batchId = id, r.Advice.Version, r.Advice.NetPayable,
                        documentId = r.Advice.DocumentId, tenantId = deps.Tenant,
                    }, ct);
                    await deps.Audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "settlement_advice", EntityId = r.Advice.AdviceId.ToString(), Action = AuditAction.StateChange,
                        ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                        DecisionOutcome = r.Outcome.ToString(), Severity = AuditSeverity.High, FieldClasses = ["financials"],
                    }, ct);
                    return Results.Created($"/api/v1/claim-batches/{id}/settlement-advice", new
                    {
                        r.Advice.AdviceId, r.Advice.Version, supersedes = r.Advice.SupersedesAdviceId,
                        r.Advice.ContentHash, documentId = r.Advice.DocumentId, r.Advice.NetPayable,
                        batchStatus = r.Batch!.Status.ToString(), frozen = r.Batch.FrozenAt is not null,
                    });
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:export"));

        // --- export (CSV / XLSX / PDF) ---------------------------------------------------------------------
        v1.MapGet("/{id:guid}/exports", async (
            Guid id, string? format, ClaimsDeps deps, SettlementService settlement, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Export, ct);
            if (denied is not null) return denied;

            var fmt = string.IsNullOrWhiteSpace(format) ? "CSV" : format!.ToUpperInvariant();
            if (fmt is not ("CSV" or "XLSX" or "PDF"))
                return Results.Problem(statusCode: 422, title: "bad-format", type: "urn:hbmp:validation", detail: "Supported formats: CSV, XLSX, PDF.");

            var r = await settlement.ExportAsync(deps.Tenant, id, fmt, deps.ProviderId, deps.Subject ?? "unknown", ct);
            switch (r.Outcome)
            {
                case ExportOutcome.NotFound: return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
                case ExportOutcome.ProviderDenied:
                    await deps.Audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "settlement_export", EntityId = id.ToString(), Action = AuditAction.Export,
                        ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                        DecisionOutcome = "Deny", DecisionReasonCode = "EXPORT_CROSS_PROVIDER", Severity = AuditSeverity.High,
                    }, ct);
                    return Results.Problem(statusCode: 403, title: "provider-isolation", type: "urn:hbmp:provider-isolation",
                        detail: "You may export only your own batch.");
                default:
                    // Audit: actor, batch, format, row count, timestamp, correlation id.
                    await deps.Audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "settlement_export", EntityId = id.ToString(), Action = AuditAction.Export,
                        ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                        DecisionOutcome = $"{r.File!.Format}:rows={r.RowCount}", Severity = AuditSeverity.Notice, FieldClasses = ["financials"],
                    }, ct);
                    return Results.File(r.File.Bytes, r.File.ContentType, r.File.FileName);
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:export"));

        // --- record an EXTERNAL payment reference (SoD: claims:settle, separate from claims:decide) ---------
        v1.MapPost("/{id:guid}/payment-reference", async (
            Guid id, PaymentReferenceBody body, ClaimsDeps deps, SettlementService settlement, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Settle, ct);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(body.Reference))
                return Results.Problem(statusCode: 422, title: "reference-required", type: "urn:hbmp:validation", detail: "An external payment reference is required.");

            var outcome = await settlement.RecordPaymentReferenceAsync(deps.Tenant, id, body.Reference, body.PaymentDate, deps.Subject ?? "unknown", ct);
            switch (outcome)
            {
                case PaymentRefOutcome.NotFound: return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
                case PaymentRefOutcome.BatchNotSettled:
                    return Results.Problem(statusCode: 409, title: "batch-not-settled", type: "urn:hbmp:conflict",
                        detail: "A payment reference can only be recorded once a settlement advice has been issued.");
                default:
                    await deps.Audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "settlement_payment_reference", EntityId = id.ToString(), Action = AuditAction.Create,
                        ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                        Severity = AuditSeverity.High, FieldClasses = ["financials"],
                    }, ct);
                    return Results.Ok(new { batchId = id, recorded = true, note = "External payment recorded; the platform initiates no transfer." });
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:settle"));
    }

    private static string? BearerOf(HttpRequest http)
    {
        var h = http.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..] : null;
    }
}

/// <summary>The external payment fact recorded by Finance after paying OUTSIDE the platform. Records only.</summary>
public sealed record PaymentReferenceBody(string Reference, DateOnly PaymentDate);
