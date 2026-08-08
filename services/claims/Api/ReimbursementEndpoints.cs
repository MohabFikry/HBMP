using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Api;

/// <summary>Phase 10b.6 — the beneficiary reimbursement channel with OCR assistance. Submit runs the pipeline
/// (validate → malware scan → OCR extract append-only → AutoMatched | ManualAssessment); confirm is the HUMAN GATE that
/// accepts the OCR values and creates the (still Pending) Reimbursement claim — no line is ever payable without an
/// explicit officer decision (10b.4). Reimbursement is capped at min(tariff, receipt); no bank/payout detail is stored.
/// Submission, OCR run, match outcome, scan rejection, and confirmation are all audited.</summary>
public static class ReimbursementEndpoints
{
    public static void MapReimbursements(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/reimbursement-requests");

        // --- submit ----------------------------------------------------------------------------------------
        v1.MapPost("", async (
            ReimbursementRequestBody body, HttpRequest http, ClaimsDeps deps, ReimbursementService svc, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.ReimburseSubmit, ct);
            if (denied is not null) return denied;

            if (body.Documents is null || body.Documents.Count == 0)
                return Results.Problem(statusCode: 422, title: "no-documents", detail: "Receipts + evidence are required.", type: "urn:hbmp:validation");

            var docs = new List<ReimbursementDoc>(body.Documents.Count);
            foreach (var d in body.Documents)
            {
                if (!Enum.TryParse<ClaimDocType>(d.DocType, true, out var dt))
                    return Results.Problem(statusCode: 422, title: "bad-doc-type", type: "urn:hbmp:validation", detail: "Unknown document type.");
                docs.Add(new ReimbursementDoc(d.DocumentId, dt, d.ContentType, d.SizeBytes));
            }

            // A provider user has no business raising a member reimbursement; the caller is Mersal staff/member acting.
            var actingFor = deps.ProviderId is null ? null : deps.Subject;
            var sub = new ReimbursementSubmission(body.BeneficiaryId, actingFor, body.ReceiptTotal,
                string.IsNullOrWhiteSpace(body.CurrencyCode) ? "EGP" : body.CurrencyCode!,
                body.LinkedOrderId, body.LinkedPrescriptionId, docs);
            var bearer = BearerOf(http);
            // 24.x — a reimbursement submission and BOTH events that route it (submitted, then matched
            // or sent for manual assessment) commit together. Lose the second and the request sits in a
            // state no worklist is watching: the beneficiary is owed money and nothing is looking at it.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            var r = await svc.SubmitAsync(deps.Tenant, deps.Subject ?? "unknown", sub, bearer, ct);

            switch (r.Outcome)
            {
                case ReimbursementOutcome.RejectedFiles:
                    await AuditReject(deps, body.BeneficiaryId, "REJECTED_FILES", r.Error);
                    return Results.Problem(statusCode: 422, title: r.Error, type: "urn:hbmp:validation", detail: "A document failed type/size validation.");
                case ReimbursementOutcome.RejectedScan:
                    await AuditReject(deps, body.BeneficiaryId, "MALWARE_SCAN_FAILED", r.Error);
                    return Results.Problem(statusCode: 422, title: "malware-scan-failed", type: "urn:hbmp:malware", detail: "A document failed the malware scan and was rejected.");
                default:
                    await deps.Outbox.EnqueueAsync("ReimbursementSubmitted.v1", "claims.events",
                        new { r.Request!.RequestId, r.Request.Status, tenantId = deps.Tenant }, ct);
                    var matchEvent = r.Outcome == ReimbursementOutcome.AutoMatched
                        ? "ReimbursementMatched.v1" : "ReimbursementRequiresManualAssessment.v1";
                    await deps.Outbox.EnqueueAsync(matchEvent, "claims.events",
                        new { r.Request.RequestId, r.Request.MatchConfidence, method = r.Request.MatchMethod.ToString(), tenantId = deps.Tenant }, ct);
                    await AuditRequest(deps, r.Request, "ReimbursementSubmitted");
                    await tx.CommitAsync(ct);
                    return Results.Created($"/api/v1/reimbursement-requests/{r.Request.RequestId}",
                        ReimbursementView.From(r.Request, await OcrOf(deps, r.Request.RequestId, ct)));
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:reimburse:submit"));

        // --- confirm (human gate) --------------------------------------------------------------------------
        v1.MapPost("/{id:guid}/confirm", async (
            Guid id, ReimbursementConfirmBody body, HttpRequest http, ClaimsDeps deps, ReimbursementService svc, CancellationToken ct) =>
        {
            // Confirmation is a Claims Officer act (review scope) — it accepts the OCR values before money is involved.
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Review, ct);
            if (denied is not null) return denied;

            // 24.x — confirmation CREATES the claim; its ClaimCreated event must not be a second commit.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            var r = await svc.ConfirmAsync(id, deps.Tenant, deps.Subject ?? "unknown",
                body.LinkedOrderId, body.LinkedPrescriptionId, BearerOf(http), ct);
            switch (r.Outcome)
            {
                case ConfirmOutcome.NotFound: return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
                case ConfirmOutcome.NotConfirmable:
                    return Results.Problem(statusCode: 409, title: "not-confirmable", type: "urn:hbmp:conflict", detail: "This request is not awaiting confirmation.");
                case ConfirmOutcome.NoAuthorizedService:
                    return Results.Problem(statusCode: 422, title: "no-authorized-service", type: "urn:hbmp:validation",
                        detail: "No single authorized underlying order/prescription resolves for this request.");
                default:
                    await deps.Outbox.EnqueueAsync("ClaimCreated.v1", "claims.events",
                        new { r.Claim!.ClaimId, origin = "Reimbursement", r.Request!.RequestId, tenantId = deps.Tenant }, ct);
                    await AuditRequest(deps, r.Request, "ReimbursementConfirmed");
                    await tx.CommitAsync(ct);
                    return Results.Ok(new { r.Request.RequestId, claimId = r.Claim.ClaimId, status = r.Request.Status.ToString() });
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:review"));

        // --- read ------------------------------------------------------------------------------------------
        v1.MapGet("/{id:guid}", async (Guid id, ClaimsDeps deps, CancellationToken ct) =>
        {
            // Deliberately the TENANT-WIDE read, not the provider-aware one: §3.4 marks
            // reimbursement_request ❌ for every provider-side role. This is the member's own out-of-pocket
            // claim with their receipts on it, and no provider is a party to it — so a provider_admin holding
            // claims:read is refused here, and that refusal is a tested rule, not an oversight.
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.ReadClaim, ct);
            if (denied is not null) return denied;

            var r = await deps.Db.ReimbursementRequests.AsNoTracking()
                .FirstOrDefaultAsync(x => x.RequestId == id && x.TenantId == deps.Tenant, ct);
            if (r is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "reimbursement_request", EntityId = id.ToString(), Action = AuditAction.Read,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, FieldClasses = ["financials"],
            }, ct);
            return Results.Ok(ReimbursementView.From(r, await OcrOf(deps, id, ct)));
        }).RequireAuthorization(HbmpPolicies.Scope("claims:read"));
    }

    private static async Task<List<OcrExtraction>> OcrOf(ClaimsDeps deps, Guid requestId, CancellationToken ct) =>
        await deps.Db.OcrExtractions.AsNoTracking().Where(x => x.RequestId == requestId).ToListAsync(ct);

    private static string? BearerOf(HttpRequest http)
    {
        var h = http.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..] : null;
    }

    private static async Task AuditRequest(ClaimsDeps deps, ReimbursementRequest r, string outcome) =>
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "reimbursement_request", EntityId = r.RequestId.ToString(), Action = AuditAction.StateChange,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
            DecisionOutcome = r.Status.ToString(), Severity = AuditSeverity.Notice, FieldClasses = ["financials"],
        });

    private static async Task AuditReject(ClaimsDeps deps, Guid beneficiaryId, string reason, string? detail) =>
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "reimbursement_request", EntityId = beneficiaryId.ToString(), Action = AuditAction.Create,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
            DecisionOutcome = "Deny", DecisionReasonCode = reason, Severity = AuditSeverity.High,
        });
}
