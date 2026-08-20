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

            // The replay is bound to the BODY, the same rule the decide path applies (migration 0011). A key
            // reused for a different beneficiary or a different set of service codes would otherwise be
            // answered with the first authorization — telling the caller their second request had been
            // raised when nothing had been raised at all, and leaving the real one unmade.
            var requestHash = IdempotencyKeyRules.Hash(
                req.Source.ToString(), req.SourceRef ?? "", req.BeneficiaryId.ToString(),
                req.RequestingProviderId?.ToString() ?? "",
                string.Join(',', (req.ServiceCodes ?? []).OrderBy(c => c, StringComparer.Ordinal)),
                req.RequestedScope ?? "", req.Priority.ToString());

            var prior = await db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
            if (prior is not null)
            {
                if (!IdempotencyKeyRules.Matches(prior.RequestHash, requestHash))
                    return Results.Problem(statusCode: 422, title: "idempotency-key-reuse",
                        type: "urn:hbmp:idempotency-key-reuse",
                        detail: "That key was already used for a different authorization. Answering it with "
                              + "the earlier one would report a request that was never raised.");

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
                EncounterId = req.EncounterId,          // ADR-0031 — the visit this came out of, if it had one
                RequestingProviderId = req.RequestingProviderId,
                ServiceCodes = Codes.Serialize(req.ServiceCodes ?? []),
                RequestedScope = string.IsNullOrWhiteSpace(req.RequestedScope) ? "{}" : req.RequestedScope!,
                Priority = req.Priority,
                Status = AuthStatus.Submitted,
                SubmittedAt = now, CreatedAt = now, UpdatedAt = now,
                /*
                 * The ORDERING CLINICIAN, falling back to the caller.
                 *
                 * `CreatedBy` is what `NotifyDecisionAsync` addresses the decision notice to, and on this
                 * endpoint the caller is a service principal holding `auth:ingest` — so it named the routing
                 * saga, the notice had no human addressee, and it was correctly not sent (§11.3). The
                 * ingesting service knows who ordered the thing; it now says so.
                 *
                 * The fallback keeps the break-glass and manual paths working unchanged: there the caller IS
                 * the human, and `me.Principal.Subject` is already the right answer.
                 */
                IdempotencyKey = idem,
                CreatedBy = string.IsNullOrWhiteSpace(req.OrderedByUserId) ? me.Principal?.Subject : req.OrderedByUserId,
            };

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Authorizations.Add(auth);
            db.ProcessedRequests.Add(new ProcessedRequest
            {
                IdempotencyKey = idem, Operation = "create-authorization",
                AuthorizationId = auth.AuthorizationId, StatusCode = 201, CreatedAt = now,
                RequestHash = requestHash,
            });
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("AuthSubmitted", "approvals.events",
                new
                {
                    // `tenantId` — this stream now feeds a consumer that binds its RLS session from the
                    // envelope (emr's care-episode consumer, ADR-0031). An untenanted message is refused
                    // there rather than applied under a guessed tenant, so the field is not optional.
                    tenantId = auth.TenantId,
                    authorizationId = auth.AuthorizationId, auth.AuthNo, beneficiaryId = auth.BeneficiaryId,
                    encounterId = auth.EncounterId,
                    source = auth.Source.ToString(),
                    // The read model's pending-queue row is keyed on priority and its SLA clock; without them
                    // every pending authorization would sit in the Routine bucket with no due time, which is
                    // the two facts an approvals dashboard exists to show.
                    priority = auth.Priority.ToString(), slaDueAt = auth.SlaDueAt,
                }, ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                DecisionOutcome = auth.Status.ToString(),
                AfterState = $"{{\"authNo\":\"{auth.AuthNo}\",\"status\":\"{auth.Status}\",\"source\":\"{auth.Source}\"}}",
            }, ct);
            await tx.CommitAsync(ct);

            return Results.Created($"/api/v1/authorizations/{auth.AuthorizationId}", AuthorizationStateView.From(auth));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:ingest"))
        .Produces<AuthorizationStateView>();

        // ---- Worklist inbox (min-necessary projection). ----
        //
        // `kind` DEFAULTS TO Review, and that default is the design (ADR-0034 Decision 3). The reviewer inbox
        // is a work queue: it means "these are waiting for you". A few hundred dispenses a day landing in it
        // would drown the twelve that need a decision, and the natural response to a queue that is mostly
        // noise is to stop reading it. Fulfilments are a REGISTER — a different question, asked deliberately,
        // by passing kind=Fulfilment. `kind=All` returns both for anyone who genuinely wants everything.
        v1.MapGet("/", async (
            string? status, string? priority, bool? slaBreached, bool? unassigned, string? kind,
            string? assignedTo, HttpResponse http,
            ApprovalsDbContext db, ApprovalsGate gate, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.List, null, "worklist", ct);
            if (denied is not null) return denied;

            var all = string.Equals(kind, "All", StringComparison.OrdinalIgnoreCase);
            var wanted = Enum.TryParse<AuthKind>(kind, ignoreCase: true, out var k) ? k : AuthKind.Review;

            var q = db.Authorizations.AsNoTracking().AsQueryable();
            if (!all) q = q.Where(a => a.Kind == wanted);

            if (Enum.TryParse<AuthStatus>(status, out var st)) q = q.Where(a => a.Status == st);
            if (Enum.TryParse<AuthPriority>(priority, out var pr)) q = q.Where(a => a.Priority == pr);
            if (slaBreached == true) q = q.Where(a => a.SlaBreached);
            if (unassigned == true) q = q.Where(a => a.AssignedReviewerId == null);

            // OWNERSHIP. `assignedTo=me` resolves to the caller; an explicit id is accepted for a supervisor
            // asking after somebody's queue. This is the axis a SHARED queue is actually worked by — "mine"
            // and "nobody has this yet" — and there was no way to ask either question: `unassigned` was
            // served and never called, and the reviewer id was not on the projection at all.
            var mine = string.Equals(assignedTo, "me", StringComparison.OrdinalIgnoreCase);
            if (mine && Guid.TryParse(me.Principal?.Subject, out var myId)) q = q.Where(a => a.AssignedReviewerId == myId);
            else if (mine) q = q.Where(a => false);          // a caller with no parseable subject owns nothing
            else if (Guid.TryParse(assignedTo, out var who)) q = q.Where(a => a.AssignedReviewerId == who);

            var now = clock.GetUtcNow();
            // A work queue is read most-urgent-first; a register is read newest-first. Fulfilments carry no
            // SLA due date at all, so the review ordering would pile every one of them at the end in the
            // order they were dispensed months ago.
            var ordered = !all && wanted == AuthKind.Fulfilment
                ? q.OrderByDescending(a => a.SubmittedAt)
                : q.OrderBy(a => a.SlaDueAt ?? DateTimeOffset.MaxValue).ThenBy(a => a.SubmittedAt);
            const int Cap = 200;
            var rows = await ordered.Take(Cap).ToListAsync(ct);
            // THE CAP NOW SAYS SO. The client filtered these 200 rows in the browser and told the reviewer
            // nothing, so a tenant with 300 pending requests narrowing to "breached" was narrowing a truncated
            // list and reading the result as the whole answer. A header, not a body wrapper: every existing
            // caller keeps the array shape it parses today.
            var total = rows.Count < Cap ? rows.Count : await q.CountAsync(ct);
            http.Headers["X-Total-Count"] = total.ToString();
            return Results.Ok(rows.Select(a => WorklistItemView.From(a, now)));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:read"))
        .Produces<IEnumerable<WorklistItemView>>();

        // ---- What was actually delivered against this authorization (ADR-0034). ----
        // Empty for a review request, which is the honest answer: nothing has been delivered against a
        // question that has not been answered.
        v1.MapGet("/{id:guid}/items", async (
            Guid id, ApprovalsDbContext db, ApprovalsGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.List, id.ToString(), "worklist", ct);
            if (denied is not null) return denied;

            if (!await db.Authorizations.AsNoTracking().AnyAsync(a => a.AuthorizationId == id, ct))
                return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var items = await db.Items.AsNoTracking()
                .Where(i => i.AuthorizationId == id)
                .OrderBy(i => i.FulfilledAt)
                .ToListAsync(ct);
            return Results.Ok(items.Select(AuthorizationItemView.From));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:read"))
        .Produces<IEnumerable<AuthorizationItemView>>();

        // ---- Worklist detail (min-necessary — still NO clinical payload; that is /review only). ----
        v1.MapGet("/{id:guid}", async (
            Guid id, ApprovalsDbContext db, ApprovalsGate gate, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.List, id.ToString(), "worklist", ct);
            if (denied is not null) return denied;

            var a = await db.Authorizations.AsNoTracking().FirstOrDefaultAsync(x => x.AuthorizationId == id, ct);
            return a is null ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found") : Results.Ok(WorklistItemView.From(a, clock.GetUtcNow()));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:read"))
        .Produces<WorklistItemView>();

        // ---- Assign: pick up a request (Submitted → UnderReview), start the SLA timer. ----
        v1.MapPost("/{id:guid}/assign", async (
            Guid id, ApprovalsDbContext db, ApprovalsGate gate, SlaOptions sla, RuleApplication engine,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.Assign, id.ToString(), "assign", ct);
            if (denied is not null) return denied;

            var auth = await db.Authorizations.FirstOrDefaultAsync(a => a.AuthorizationId == id, ct);
            if (auth is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            if (!AuthorizationWorkflow.CanTransition(auth.Status, AuthStatus.UnderReview))
                return Results.Problem(statusCode: 409, title: "illegal-transition",
                    detail: $"Cannot assign a request in status {auth.Status}.", type: "urn:hbmp:illegal-transition",
                    extensions: new Dictionary<string, object?> { ["status"] = auth.Status.ToString() });

            var now = clock.GetUtcNow();
            var reviewerId = Guid.TryParse(me.Principal?.Subject, out var rg) ? rg : Guid.Empty;
            var before = auth.Status;
            auth.Status = AuthStatus.UnderReview;
            auth.AssignedReviewerId = reviewerId;

            // ADR-0035 §5.4. The engine may change WHERE this sits and HOW LONG the reviewer has — nothing
            // else. `SlaOptions.DueFrom` stays the fallback: a request whose rules could not be read keeps
            // the priority-based deadline rather than losing one, because a request with no deadline is worse
            // than a request with a generic one.
            var outcome = await engine.ForAsync(auth, now, ct);
            auth.RoutedQueue = outcome.Queue;
            auth.RoutedByRule = outcome.RoutedByRule;
            auth.SlaDueAt = outcome.SlaHours is { } h ? now.AddHours(h) : sla.DueFrom(auth.Priority, now);
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
                new
                {
                    tenantId = auth.TenantId,
                    authorizationId = auth.AuthorizationId, auth.AuthNo, reviewerId,
                    slaDueAt = auth.SlaDueAt, priority = auth.Priority.ToString(),
                    // Carried on the event so a consumer can see WHY this landed where it did without
                    // re-deriving it against a rule set that may since have moved on.
                    queue = auth.RoutedQueue, routedByRule = auth.RoutedByRule,
                }, ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                BeforeState = before.ToString(), AfterState = auth.Status.ToString(), DecisionOutcome = "UnderReview",
            }, ct);
            await tx.CommitAsync(ct);

            return Results.Ok(AuthorizationStateView.From(auth));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:review"))
        .Produces<AuthorizationStateView>();
    }
}
