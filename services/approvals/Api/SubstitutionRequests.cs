using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>
/// A lab or imaging technician asking whether another examination may stand in for the one ordered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a question and not a picker.</b> The dispensing counter substitutes from the drug's ATC-5
/// class — a clinically-sound equivalence set that exists in master data — and the server refuses anything
/// outside it. Nothing equivalent exists for examinations: <c>examination_type</c> records a category and a
/// sensitivity, and neither says that one test may stand in for another. A list derived from the category
/// would put "any radiology procedure" behind a button, which is a technician prescribing.
/// </para>
/// <para>
/// So the honest version of "we do not know what is equivalent" is to ask someone who does. The request lands
/// in the queue the approval team already works, next to the validity extensions raised by the same people
/// for the same reason: the patient is here, the document is wrong, and the recovery should not be a second
/// journey.
/// </para>
/// <para>
/// It shares the shape of <see cref="ValidityExtensionEndpoints"/> deliberately, down to the mandatory reason
/// and the one-open-request rule, because it is the same act by the same person under the same pressure.
/// </para>
/// </remarks>
public static class SubstitutionRequestEndpoints
{
    public static void MapSubstitutionRequests(this WebApplication app)
    {
        app.MapPost("/api/v1/authorizations/substitution-requests", async (
            RequestSubstitutionRequest req, HttpRequest http,
            ApprovalsDbContext db, ApprovalsGate gate, AuthNoIssuer authNos,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
            CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "missing-idempotency-key",
                    detail: "An Idempotency-Key header is required.", type: "urn:hbmp:missing-idempotency-key");

            var denied = await gate.CheckAsync(ApprovalsPolicies.RequestSubstitution, req.OrderLineId.ToString(), "request-substitution", ct);
            if (denied is not null) return denied;

            // The reason is the whole substance. An approver deciding "may they run something else instead"
            // with an empty box is deciding on who asked rather than on why — and unlike a dispensing
            // substitution, nobody downstream can infer the answer from a formulary.
            if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length < 10)
                return Results.Problem(statusCode: 422, title: "reason-required", type: "urn:hbmp:validation",
                    detail: "Say why the ordered examination cannot be performed as written — at least a "
                            + "short sentence. The approver has nothing else to decide on.");

            var prior = await db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
            if (prior is not null)
            {
                var already = await db.Authorizations.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.AuthorizationId == prior.AuthorizationId, ct);
                return already is null ? Results.NoContent() : Results.Ok(AuthorizationStateView.From(already));
            }

            // One OPEN request per line. A bench that gets no answer in a minute raises a second and a third,
            // and the approval team works the same question three times while the technician watches three
            // rows that all say Submitted.
            var open = await db.Authorizations.AsNoTracking().FirstOrDefaultAsync(a =>
                a.Kind == AuthKind.Review && a.Source == AuthSource.OrderLine
                && a.SourceRef == req.OrderLineId.ToString()
                && (a.Status == AuthStatus.Submitted || a.Status == AuthStatus.UnderReview
                    || a.Status == AuthStatus.InfoRequested), ct);
            if (open is not null)
                return Results.Conflict(new
                {
                    title = "substitution-already-requested",
                    detail = $"{open.AuthNo} is already with the approval team for this line.",
                    authorizationId = open.AuthorizationId, authNo = open.AuthNo, status = open.Status.ToString(),
                });

            if (!Guid.TryParse(me.Principal?.ProviderId, out var providerId))
                return Results.Problem(statusCode: 422, title: "requesting-provider-required", type: "urn:hbmp:validation",
                    detail: "This account is not bound to a provider, so there is nobody to raise the request on "
                            + "behalf of. A provider-scoped role must have a provider on its membership.");

            var now = clock.GetUtcNow();
            var auth = new Authorization
            {
                AuthorizationId = Guid.NewGuid(),
                AuthNo = await authNos.NextAsync(now.Year, ct),
                BeneficiaryId = req.BeneficiaryId,
                Kind = AuthKind.Review,
                Source = AuthSource.OrderLine,
                SourceRef = req.OrderLineId.ToString(),
                RequestingProviderId = providerId,
                // The ORDERED code, so the queue row says what was asked for. The proposal, if there is one,
                // is in the scope below — a suggestion is not an authorized service until somebody says so.
                ServiceCodes = Codes.Serialize(string.IsNullOrWhiteSpace(req.OrderedCode) ? [] : [req.OrderedCode!]),
                RequestedScope = System.Text.Json.JsonSerializer.Serialize(new
                {
                    kind = "substitution",
                    itemRef = req.OrderReference,
                    orderId = req.OrderId,
                    orderLineId = req.OrderLineId,
                    orderedCode = req.OrderedCode,
                    orderedLabel = req.OrderedLabel,
                    proposedCode = req.ProposedCode,
                    reason = req.Reason!.Trim(),
                }),
                Priority = AuthPriority.Routine,
                Status = AuthStatus.Submitted,
                SubmittedAt = now, CreatedAt = now, UpdatedAt = now,
                IdempotencyKey = idem,
                CreatedBy = me.Principal?.Subject,
            };

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Authorizations.Add(auth);
            db.ProcessedRequests.Add(new ProcessedRequest
            {
                IdempotencyKey = idem, Operation = "request-substitution",
                AuthorizationId = auth.AuthorizationId, StatusCode = 201, CreatedAt = now,
            });
            await outbox.EnqueueAsync("SubstitutionRequested", "approvals.events", new
            {
                tenantId = auth.TenantId, authorizationId = auth.AuthorizationId, auth.AuthNo,
                orderId = req.OrderId, orderLineId = req.OrderLineId, auth.BeneficiaryId,
                requestedByUserId = auth.CreatedBy,
            }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, TenantId = auth.TenantId,
                AfterState = $"{{\"authNo\":\"{auth.AuthNo}\",\"source\":\"OrderLine\",\"kind\":\"substitution\"}}",
                Purpose = "substitution-request", Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Created($"/api/v1/authorizations/{auth.AuthorizationId}", AuthorizationStateView.From(auth));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:request-substitution"));
    }
}

/// <param name="OrderReference">The human reference — ORD-2026-000900. Display only; the decision travels on
/// the ids.</param>
/// <param name="ProposedCode">What the bench suggests instead, if they have a suggestion. OPTIONAL, and
/// optional on purpose: "we cannot run this one" is a complete and useful request, and requiring a proposal
/// would push a technician into naming a test they are not qualified to choose.</param>
public sealed record RequestSubstitutionRequest(
    Guid OrderId,
    Guid OrderLineId,
    string? OrderReference,
    Guid BeneficiaryId,
    string? OrderedCode,
    string? OrderedLabel,
    string? ProposedCode,
    string? Reason);
