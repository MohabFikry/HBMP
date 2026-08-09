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
/// Raising a request to revalidate something that has expired.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of the ingestion seam in <c>Worklist.cs</c>, but for a HUMAN caller who is not on the
/// approval team. A pharmacist or a lab / imaging technician is holding a lapsed prescription or order with
/// the patient in front of them; this is how that becomes a question the approval team can answer, instead
/// of a wasted journey back to a doctor.
/// </para>
/// <para>
/// <b>Why a separate endpoint rather than a parameter on the ingestion one.</b> That endpoint takes a free
/// choice of source, beneficiary, service codes and requested scope, and is scoped <c>auth:ingest</c> —
/// machine-only. Handing a pharmacist any of it would let them author an arbitrary authorization. This one
/// accepts an expired item's id and a reason, and can produce nothing else.
/// </para>
/// </remarks>
public static class ValidityExtensionEndpoints
{
    public static void MapValidityExtensions(this WebApplication app)
    {
        app.MapPost("/api/v1/authorizations/validity-extensions", async (
            RequestValidityExtensionRequest req, HttpRequest http,
            ApprovalsDbContext db, ApprovalsGate gate, AuthNoIssuer authNos, IValidityExtensionApplier applier,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
            CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "missing-idempotency-key",
                    detail: "An Idempotency-Key header is required.", type: "urn:hbmp:missing-idempotency-key");

            var denied = await gate.CheckAsync(ApprovalsPolicies.RequestExtension, req.ItemId.ToString(), "request-extension", ct);
            if (denied is not null) return denied;

            if (!Enum.TryParse<ExtendableItem>(req.ItemType, ignoreCase: true, out var itemType))
                return Results.Problem(statusCode: 422, title: "unknown-item-type", type: "urn:hbmp:validation",
                    detail: "itemType must be Prescription or InvestigationOrder.");

            // A reason is MANDATORY and is the whole substance of the request. An approver deciding "should
            // this patient get another ten days" with an empty box in front of them is deciding on nothing,
            // and the answer would come down to who asked rather than why.
            if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length < 10)
                return Results.Problem(statusCode: 422, title: "reason-required", type: "urn:hbmp:validation",
                    detail: "Say why this needs extending — at least a short sentence. The approver has "
                            + "nothing else to decide on.");

            var prior = await db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
            if (prior is not null)
            {
                var already = await db.Authorizations.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.AuthorizationId == prior.AuthorizationId, ct);
                return already is null ? Results.NoContent() : Results.Ok(AuthorizationStateView.From(already));
            }

            // One OPEN request per item. Without this, a counter that gets no answer within a minute raises a
            // second and a third, and the approval team works the same question three times while the
            // pharmacist watches three rows that all say Submitted.
            var open = await db.Authorizations.AsNoTracking().FirstOrDefaultAsync(a =>
                a.Source == AuthSource.ValidityExtension && a.SourceRef == req.ItemId.ToString()
                && (a.Status == AuthStatus.Submitted || a.Status == AuthStatus.UnderReview
                    || a.Status == AuthStatus.InfoRequested), ct);
            if (open is not null)
                return Results.Conflict(new
                {
                    title = "extension-already-requested",
                    detail = $"{open.AuthNo} is already with the approval team for this item.",
                    authorizationId = open.AuthorizationId, authNo = open.AuthNo, status = open.Status.ToString(),
                });

            // The requester's OWN provider. A pharmacist's token carries it (provider-scoped role), and it is
            // who the decision is about: this pharmacy is asking to dispense this prescription.
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
                Source = AuthSource.ValidityExtension,
                SourceRef = req.ItemId.ToString(),
                RequestingProviderId = providerId,
                ServiceCodes = "[]",
                // The itemType is what tells the decision path WHICH service to call back on approval, and
                // the reference is what the approver reads in the queue. Both belong on the request.
                RequestedScope = System.Text.Json.JsonSerializer.Serialize(new
                {
                    kind = "validity-extension",
                    itemType = itemType.ToString(),
                    itemRef = req.ItemReference,
                    expiredAt = req.ExpiredAt,
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
                IdempotencyKey = idem, Operation = "request-validity-extension",
                AuthorizationId = auth.AuthorizationId, CreatedAt = now,
            });
            await outbox.EnqueueAsync("ValidityExtensionRequested", "approvals.events", new
            {
                tenantId = auth.TenantId, authorizationId = auth.AuthorizationId, auth.AuthNo,
                itemType = itemType.ToString(), itemId = req.ItemId, auth.BeneficiaryId,
                requestedByUserId = auth.CreatedBy,
            }, ct);
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, TenantId = auth.TenantId,
                AfterState = $"{{\"authNo\":\"{auth.AuthNo}\",\"source\":\"ValidityExtension\",\"itemType\":\"{itemType}\"}}",
                Purpose = "validity-extension", Severity = AuditSeverity.Notice,
            }, ct);
            await tx.CommitAsync(ct);

            _ = applier;   // resolved here so a misconfigured callback fails at startup, not at decision time

            return Results.Created($"/api/v1/authorizations/{auth.AuthorizationId}", AuthorizationStateView.From(auth));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:request-extension"))
        .Produces<AuthorizationStateView>();
    }
}

/// <summary>What can be revalidated. The value decides which service the approval calls back on.</summary>
public enum ExtendableItem { Prescription, InvestigationOrder }

/// <param name="ItemReference">The human reference — RX-2026-000312 / ORD-2026-000900. Display only, so the
/// approver reads what the pharmacist is holding rather than a uuid; the decision travels on
/// <paramref name="ItemId"/>.</param>
public sealed record RequestValidityExtensionRequest(
    string ItemType, Guid ItemId, string? ItemReference, Guid BeneficiaryId,
    DateTimeOffset? ExpiredAt, string? Reason);
