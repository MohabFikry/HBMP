using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>Phase 7.1 — the reviewer worklist. Ingestion (the routing-saga / event-consumer seam) creates
/// Submitted requests; the inbox lists them (min-necessary, NO clinical payload); assign picks one up
/// (Submitted → UnderReview, starts the SLA timer, emits AuthUnderReview). The clinical review view lives in
/// <see cref="Review"/>. All state changes are audited; illegal transitions are an RFC7807 409.</summary>
public static class Worklist
{
    public static void MapWorklist(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/authorizations");

        // ---- Ingestion: create a Submitted authorization from a routed source (or a manual seed). ----
        // System-to-system seam (scope auth:ingest) — this is what the phase-4 routing saga / the
        // OrderPendingApproval|RxSubmitted event consumer calls. No clinical payload crosses here.
        v1.MapPost("/", async (
            CreateAuthorizationRequest req, HttpRequest http,
            ApprovalsDbContext db, AuthNoIssuer authNos, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "missing-idempotency-key",
                    detail: "An Idempotency-Key header is required.", type: "urn:hbmp:missing-idempotency-key");

            if (req.Source != AuthSource.Manual && req.RequestingProviderId is null)
                return Results.Problem(statusCode: 422, title: "requesting-provider-required",
                    detail: "A non-manual authorization must name the requesting provider.", type: "urn:hbmp:validation");

            var prior = await db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
            if (prior is not null)
            {
                var existing = await db.Authorizations.AsNoTracking().FirstOrDefaultAsync(a => a.AuthorizationId == prior.AuthorizationId, ct);
                return existing is null ? Results.NoContent() : Results.Ok(AuthorizationStateView.From(existing));
            }

            var now = clock.GetUtcNow();
            var auth = new Authorization
            {
                AuthorizationId = Guid.NewGuid(),
                AuthNo = await authNos.NextAsync(now.Year, ct),
                BeneficiaryId = req.BeneficiaryId,
                Source = req.Source,
                SourceRef = req.SourceRef,
                RequestingProviderId = req.RequestingProviderId,
                ServiceCodes = Codes.Serialize(req.ServiceCodes ?? []),
                RequestedScope = string.IsNullOrWhiteSpace(req.RequestedScope) ? "{}" : req.RequestedScope!,
                Priority = req.Priority,
                Status = AuthStatus.Submitted,
                SubmittedAt = now, CreatedAt = now, UpdatedAt = now,
                IdempotencyKey = idem, CreatedBy = me.Principal?.Subject,
            };

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Authorizations.Add(auth);
            db.ProcessedRequests.Add(new ProcessedRequest
            {
                IdempotencyKey = idem, Operation = "create-authorization",
                AuthorizationId = auth.AuthorizationId, StatusCode = 201, CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("AuthSubmitted", "approvals.events",
                new { authorizationId = auth.AuthorizationId, auth.AuthNo, beneficiaryId = auth.BeneficiaryId, source = auth.Source.ToString() }, ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                DecisionOutcome = auth.Status.ToString(),
                AfterState = $"{{\"authNo\":\"{auth.AuthNo}\",\"status\":\"{auth.Status}\",\"source\":\"{auth.Source}\"}}",
            }, ct);

            return Results.Created($"/api/v1/authorizations/{auth.AuthorizationId}", AuthorizationStateView.From(auth));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:ingest"));

        // ---- Worklist inbox (min-necessary projection). ----
        v1.MapGet("/", async (
            string? status, string? priority, bool? slaBreached, bool? unassigned,
            ApprovalsDbContext db, ApprovalsGate gate, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.List, null, "worklist", ct);
            if (denied is not null) return denied;

            var q = db.Authorizations.AsNoTracking().AsQueryable();
            if (Enum.TryParse<AuthStatus>(status, out var st)) q = q.Where(a => a.Status == st);
            if (Enum.TryParse<AuthPriority>(priority, out var pr)) q = q.Where(a => a.Priority == pr);
            if (slaBreached == true) q = q.Where(a => a.SlaBreached);
            if (unassigned == true) q = q.Where(a => a.AssignedReviewerId == null);

            var now = clock.GetUtcNow();
            var items = await q.OrderBy(a => a.SlaDueAt ?? DateTimeOffset.MaxValue).ThenBy(a => a.SubmittedAt)
                .Take(200).ToListAsync(ct);
            return Results.Ok(items.Select(a => WorklistItemView.From(a, now)));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:read"));

        // ---- Worklist detail (min-necessary — still NO clinical payload; that is /review only). ----
        v1.MapGet("/{id:guid}", async (
            Guid id, ApprovalsDbContext db, ApprovalsGate gate, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.List, id.ToString(), "worklist", ct);
            if (denied is not null) return denied;

            var a = await db.Authorizations.AsNoTracking().FirstOrDefaultAsync(x => x.AuthorizationId == id, ct);
            return a is null ? Results.NotFound() : Results.Ok(WorklistItemView.From(a, clock.GetUtcNow()));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:read"));

        // ---- Assign: pick up a request (Submitted → UnderReview), start the SLA timer. ----
        v1.MapPost("/{id:guid}/assign", async (
            Guid id, ApprovalsDbContext db, ApprovalsGate gate, SlaOptions sla,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.Assign, id.ToString(), "assign", ct);
            if (denied is not null) return denied;

            var auth = await db.Authorizations.FirstOrDefaultAsync(a => a.AuthorizationId == id, ct);
            if (auth is null) return Results.NotFound();

            if (!AuthorizationWorkflow.CanTransition(auth.Status, AuthStatus.UnderReview))
                return Results.Problem(statusCode: 409, title: "illegal-transition",
                    detail: $"Cannot assign a request in status {auth.Status}.", type: "urn:hbmp:illegal-transition",
                    extensions: new Dictionary<string, object?> { ["status"] = auth.Status.ToString() });

            var now = clock.GetUtcNow();
            var reviewerId = Guid.TryParse(me.Principal?.Subject, out var rg) ? rg : Guid.Empty;
            var before = auth.Status;
            auth.Status = AuthStatus.UnderReview;
            auth.AssignedReviewerId = reviewerId;
            auth.SlaDueAt = sla.DueFrom(auth.Priority, now);
            auth.UpdatedAt = now;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                // Another reviewer picked it up first (xmin moved) → 409, no state change.
                return Results.Problem(statusCode: 409, title: "already-assigned",
                    detail: "This request was picked up by another reviewer.", type: "urn:hbmp:already-assigned");
            }
            await outbox.EnqueueAsync("AuthUnderReview", "approvals.events",
                new { authorizationId = auth.AuthorizationId, auth.AuthNo, reviewerId, slaDueAt = auth.SlaDueAt }, ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                BeforeState = before.ToString(), AfterState = auth.Status.ToString(), DecisionOutcome = "UnderReview",
            }, ct);

            return Results.Ok(AuthorizationStateView.From(auth));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:review"));
    }
}
