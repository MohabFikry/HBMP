using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Api;

/// <summary>Phase 10b.5 — the provider-submitted origination channel. A provider (or Mersal on their behalf) submits an
/// invoice; each line is matched to a delivered/authorized fulfillment. Matched → priced payable line (billed recorded
/// alongside contract, variance flagged); unmatched → NO_FULFILLMENT_RECORD manual line (never auto-approved);
/// re-submission of an already-claimed fulfillment → DUPLICATE_CLAIM (the 10b.1 index fires). Providers are isolated to
/// their OWN submissions (ABAC PO + RLS). Every submission, document attach, and match/no-match outcome is audited.</summary>
public static class SubmissionEndpoints
{
    public static void MapSubmissions(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/claims/submissions");

        // --- submit ----------------------------------------------------------------------------------------
        v1.MapPost("", async (
            SubmissionRequestBody body, HttpRequest http, ClaimsDeps deps, SubmissionService submissions,
            CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Submit, ct);
            if (denied is not null) return denied;

            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "idempotency-key-required",
                    detail: "The Idempotency-Key header is required on a submission.", type: "urn:hbmp:idempotency-required");

            if (body.Lines is null || body.Lines.Count == 0)
                return Results.Problem(statusCode: 422, title: "no-lines", detail: "A submission needs at least one line.", type: "urn:hbmp:validation");

            // ABAC provider isolation: a provider user may submit ONLY for their own provider. A Mersal user (no
            // provider affiliation) may submit on a provider's behalf — recorded for accountability.
            string? onBehalfOf = null;
            if (deps.ProviderId is { } pid && Guid.TryParse(pid, out var callerProvider))
            {
                if (callerProvider != body.ProviderId)
                {
                    await AuditDenied(deps, body.ProviderId, "SUBMIT_CROSS_PROVIDER");
                    return Results.Problem(statusCode: 403, title: "provider-isolation", type: "urn:hbmp:provider-isolation",
                        detail: "You may submit claims only for your own provider.");
                }
            }
            else onBehalfOf = deps.Subject; // Mersal staff acting on the provider's behalf.

            var parsed = ParseLines(body.Lines);
            if (parsed is null)
                return Results.Problem(statusCode: 422, title: "bad-line", detail: "A line has an unknown code system.", type: "urn:hbmp:validation");

            var req = new SubmissionRequest(body.ProviderId, body.BeneficiaryId, body.InvoiceNumber,
                string.IsNullOrWhiteSpace(body.CurrencyCode) ? "EGP" : body.CurrencyCode!, onBehalfOf, parsed);
            var bearer = BearerOf(http);
            // 24.x — the claim and its ClaimSubmitted event commit together. SubmissionService joins
            // this transaction rather than opening its own, so a crash between the two cannot leave a
            // submitted claim that adjudication, batching and the provider portal never hear about.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            var r = await submissions.SubmitAsync(deps.Tenant, deps.Subject ?? "unknown", req, idem, bearer, ct);

            switch (r.Outcome)
            {
                case SubmitOutcome.IdempotencyKeyReuse:
                    return Results.Problem(statusCode: 422, title: "idempotency-key-reuse",
                        type: "urn:hbmp:idempotency-key-reuse",
                        detail: "That key was already used for a different submission. Answering it with the "
                              + "earlier claim would report an invoice as received that was never received.");
                case SubmitOutcome.Duplicate:
                    await AuditDenied(deps, body.ProviderId, "DUPLICATE_CLAIM");
                    return Results.Problem(statusCode: 409, title: "duplicate-claim", type: "urn:hbmp:duplicate-claim",
                        detail: "A submitted line references a fulfillment that is already claimed; no second payable line is created.");
                case SubmitOutcome.Replayed:
                    return Results.Ok(SubmissionView.From(r.Submission!));
                default:
                    await AuditSubmission(deps, r.Submission!);
                    if (r.Claim is not null)
                        await deps.Outbox.EnqueueAsync("ClaimSubmitted.v1", "claims.events", new
                        {
                            r.Submission!.SubmissionId, r.Claim.ClaimId, providerId = body.ProviderId,
                            status = r.Submission.Status.ToString(), tenantId = deps.Tenant,
                        }, ct);
                    await tx.CommitAsync(ct);
                    return Results.Created($"/api/v1/claims/submissions/{r.Submission!.SubmissionId}", SubmissionView.From(r.Submission));
            }
        }).RequireAuthorization(HbmpPolicies.Scope("claims:submit"))
        .Produces<SubmissionView>();

        // --- attach a document reference -------------------------------------------------------------------
        v1.MapPost("/{id:guid}/documents", async (
            Guid id, AttachDocumentBody body, ClaimsDeps deps, SubmissionService submissions, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(ClaimsPolicies.Submit, ct);
            if (denied is not null) return denied;

            var typeErr = DocumentValidation.Validate(body.ContentType, body.SizeBytes);
            if (typeErr is not null)
                return Results.Problem(statusCode: 422, title: typeErr, type: "urn:hbmp:validation",
                    detail: "The document type or size is not accepted.");
            if (!Enum.TryParse<ClaimDocType>(body.DocType, true, out var docType))
                return Results.Problem(statusCode: 422, title: "bad-doc-type", type: "urn:hbmp:validation", detail: "Unknown document type.");

            var doc = await submissions.AttachDocumentAsync(id, deps.Tenant, body.DocumentId, docType, deps.Subject ?? "unknown", ct);
            if (doc is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "claim_document", EntityId = doc.ClaimDocumentId.ToString(), Action = AuditAction.Create,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                Severity = AuditSeverity.Notice, FieldClasses = ["documents"],
            }, ct);
            return Results.Created($"/api/v1/claims/submissions/{id}/documents/{doc.ClaimDocumentId}",
                new { doc.ClaimDocumentId, doc.DocumentId, docType = doc.DocType.ToString() });
        }).RequireAuthorization(HbmpPolicies.Scope("claims:submit"));

        // --- read (provider-isolated) ----------------------------------------------------------------------
        v1.MapGet("/{id:guid}", async (Guid id, ClaimsDeps deps, CancellationToken ct) =>
        {
            // The provider that FILED this invoice may read it back (§3.4, claim_document R🟠PO own
            // submissions); the isolation below then decides which one that is.
            var denied = await deps.Gate.CheckClaimReadAsync(ct);
            if (denied is not null) return denied;

            var s = await deps.Db.ClaimSubmissions.AsNoTracking().Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.SubmissionId == id && x.TenantId == deps.Tenant, ct);
            // Provider isolation at the app layer (RLS is the second line): a provider sees only its own submissions.
            if (s is null || (deps.ProviderId is { } pid && Guid.TryParse(pid, out var cp) && s.ProviderId != cp))
                return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "claim_submission", EntityId = id.ToString(), Action = AuditAction.Read,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
                FieldClasses = ["financials"],
            }, ct);
            return Results.Ok(SubmissionView.From(s));
        }).RequireAuthorization(HbmpPolicies.Scope("claims:read"))
        .Produces<SubmissionView>();
    }

    private static IReadOnlyList<SubmissionLineInput>? ParseLines(IReadOnlyList<SubmissionLineBody> lines)
    {
        var result = new List<SubmissionLineInput>(lines.Count);
        foreach (var l in lines)
        {
            if (!Enum.TryParse<ClaimCodeSystem>(l.CodeSystem, true, out var cs)) return null;
            result.Add(new SubmissionLineInput(cs, l.Code, l.Description, l.ServiceDate, l.Quantity, l.BilledAmount, l.AuthorizationId));
        }
        return result;
    }

    private static string? BearerOf(HttpRequest http)
    {
        var h = http.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..] : null;
    }

    private static async Task AuditSubmission(ClaimsDeps deps, ClaimSubmission s)
    {
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "claim_submission", EntityId = s.SubmissionId.ToString(), Action = AuditAction.Create,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
            DecisionOutcome = s.Status.ToString(), Severity = AuditSeverity.Notice, FieldClasses = ["financials"],
        });
    }

    private static async Task AuditDenied(ClaimsDeps deps, Guid providerId, string reason)
    {
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "claim_submission", EntityId = providerId.ToString(), Action = AuditAction.Create,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant, ProviderId = deps.ProviderId,
            DecisionOutcome = "Deny", DecisionReasonCode = reason, Severity = AuditSeverity.High,
        });
    }
}
