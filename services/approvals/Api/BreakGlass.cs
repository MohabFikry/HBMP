using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>Phase 7.3 — the break-glass and manual paths (US-061, US-062). These are the specially-audited
/// exceptions: emergency approval and director override (Director-only) and manual authorization all require a
/// non-blank justification (422 otherwise), write a break_glass decision row (flagged, High-severity audit) and
/// mark the case for retrospective review. Also exposes the TAT/SLA aggregate for the reporting read-model.</summary>
public static class BreakGlass
{
    public static void MapBreakGlass(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/authorizations");

        // EMERGENCY APPROVAL (US-061): Submitted → EmergencyApproved, Director only.
        v1.MapPost("/{id:guid}/emergency-approve", async (Guid id, EmergencyApproveRequest req, HttpRequest http, DecisionDeps deps, CancellationToken ct) =>
        {
            if (DecisionRules.IsBlank(req.Justification))
                return Results.Problem(statusCode: 422, title: "justification-required",
                    detail: "Emergency approval requires a justification.", type: "urn:hbmp:validation");
            return await Decisions.Decide(id, AuthDecision.EmergencyApproved, req.Justification, null, http, deps, ct,
                breakGlass: true, justification: req.Justification);
        }).RequireAuthorization(HbmpPolicies.Scope("auth:emergency"));

        // OVERRIDE (US-061): Rejected → Overridden, Director only. Releases downstream, tagged as an override.
        v1.MapPost("/{id:guid}/override", async (Guid id, OverrideRequest req, HttpRequest http, DecisionDeps deps, CancellationToken ct) =>
        {
            if (DecisionRules.IsBlank(req.Justification))
                return Results.Problem(statusCode: 422, title: "justification-required",
                    detail: "A director override requires a justification.", type: "urn:hbmp:validation");
            return await Decisions.Decide(id, AuthDecision.Overridden, req.Justification, null, http, deps, ct,
                breakGlass: true, justification: req.Justification);
        }).RequireAuthorization(HbmpPolicies.Scope("auth:override"));

        // MANUAL AUTHORIZATION (US-062): create without a provider submission, then decide immediately.
        v1.MapPost("/manual", async (
            ManualAuthorizationRequest req, HttpRequest http,
            ApprovalsDbContext db, ApprovalsGate gate, AuthNoIssuer authNos,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "missing-idempotency-key",
                    detail: "An Idempotency-Key header is required.", type: "urn:hbmp:missing-idempotency-key");
            if (DecisionRules.IsBlank(req.Justification))
                return Results.Problem(statusCode: 422, title: "justification-required",
                    detail: "A manual authorization requires a justification.", type: "urn:hbmp:validation");
            if (req.Decision is not (AuthDecision.Approved or AuthDecision.PartiallyApproved))
                return Results.Problem(statusCode: 422, title: "invalid-decision",
                    detail: "A manual authorization must be Approved or PartiallyApproved.", type: "urn:hbmp:validation");

            var denied = await gate.CheckAsync(ApprovalsPolicies.Manual, null, "break-glass", ct);
            if (denied is not null) return denied;

            // Idempotent replay.
            var prior = await db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
            if (prior is not null)
            {
                var a0 = await db.Authorizations.AsNoTracking().Include(a => a.Decisions)
                    .FirstOrDefaultAsync(a => a.AuthorizationId == prior.AuthorizationId, ct);
                return a0 is null ? Results.NoContent() : Results.Ok(DecisionView.From(a0, a0.Decisions.OrderByDescending(d => d.DecidedAt).First()));
            }

            var requested = req.ServiceCodes ?? [];
            string? approvedScopeJson = null;
            if (req.Decision == AuthDecision.PartiallyApproved)
            {
                var err = DecisionRules.ValidatePartialScope(requested, req.ApprovedScope ?? []);
                if (err != PartialScopeError.None)
                    return Results.Problem(statusCode: 422, title: "invalid-approved-scope",
                        detail: "approved_scope must be a non-empty strict subset of the requested codes.", type: "urn:hbmp:validation");
                approvedScopeJson = Codes.Serialize(req.ApprovedScope!);
            }

            var now = clock.GetUtcNow();
            var reviewerId = Guid.TryParse(me.Principal?.Subject, out var rg) ? rg : Guid.Empty;
            var auth = new Authorization
            {
                AuthorizationId = Guid.NewGuid(), AuthNo = await authNos.NextAsync(now.Year, ct),
                BeneficiaryId = req.BeneficiaryId, Source = AuthSource.Manual, RequestingProviderId = null,
                ServiceCodes = Codes.Serialize(requested),
                RequestedScope = string.IsNullOrWhiteSpace(req.RequestedScope) ? "{}" : req.RequestedScope!,
                Priority = AuthPriority.Routine, Status = req.Decision == AuthDecision.Approved ? AuthStatus.Approved : AuthStatus.PartiallyApproved,
                SubmittedAt = now, DecidedAt = now, TatSeconds = 0, RetrospectiveReviewRequired = true,
                AssignedReviewerId = reviewerId, CreatedAt = now, UpdatedAt = now, IdempotencyKey = idem, CreatedBy = me.Principal?.Subject,
            };
            var row = new AuthorizationDecision
            {
                DecisionId = Guid.NewGuid(), AuthorizationId = auth.AuthorizationId, Decision = req.Decision,
                ReviewerId = reviewerId, DecidedAt = now, Rationale = req.Rationale, ApprovedScope = approvedScopeJson,
                BreakGlass = true, Justification = req.Justification, CorrelationId = http.HttpContext?.TraceIdentifier,
            };
            auth.Decisions.Add(row);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Authorizations.Add(auth);
            db.ProcessedRequests.Add(new ProcessedRequest
            {
                IdempotencyKey = idem, Operation = $"manual:{req.Decision}", AuthorizationId = auth.AuthorizationId,
                StatusCode = 201, CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync(req.Decision == AuthDecision.Approved ? "AuthApproved" : "AuthPartiallyApproved", "approvals.events", new
            {
                // `tenantId` — the ordinary decision path in Decisions.cs carries it and this one did not, so
                // every manual and emergency approval was dead-lettered by the reporting consumer and by
                // emr's care-episode consumer, both of which refuse a message they cannot attribute. The
                // approval-TAT report was therefore missing exactly the decisions a supervisor most wants to
                // see, and missing them silently.
                tenantId = auth.TenantId,
                authorizationId = auth.AuthorizationId, auth.AuthNo, beneficiaryId = auth.BeneficiaryId,
                source = "Manual", approvedScope = approvedScopeJson is null ? null : Codes.Parse(approvedScopeJson),
                releasesDownstream = true, breakGlass = true,
            }, ct);
            // No notification copy here, deliberately. A manual/emergency authorization is CREATED by the
            // person deciding it, so `CreatedBy` is the decider — and telling somebody the thing they just did
            // has been done is the noise that teaches a team to ignore the channel. The retrospective-review
            // flag and the audit event are what make this decision visible to somebody else.

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.Decision,
                ActorUserId = me.Principal?.Subject, ActorRole = string.Join(',', me.Principal?.Roles ?? new HashSet<string>()),
                TenantId = me.Principal?.TenantId, AfterState = auth.Status.ToString(),
                DecisionOutcome = req.Decision.ToString(), DecisionReasonCode = req.Rationale, BreakGlass = true,
                Severity = AuditSeverity.High,
            }, ct);
            await tx.CommitAsync(ct);

            return Results.Created($"/api/v1/authorizations/{auth.AuthorizationId}", DecisionView.From(auth, row));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:manual"))
        .Produces<DecisionView>();

        // RETROSPECTIVE-REVIEW QUEUE: break-glass cases awaiting post-hoc review (min-necessary, no clinical payload).
        //
        // `closed=true` returns the reviewed half instead. "What was concluded about last month's overrides" is
        // the other question asked of this table, and until now it had no answer at all — see the POST below.
        v1.MapGet("/retrospective-queue", async (
            bool? closed, ApprovalsDbContext db, ApprovalsGate gate, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.List, null, "retrospective", ct);
            if (denied is not null) return denied;
            var now = clock.GetUtcNow();
            var wantClosed = closed == true;
            var q = db.Authorizations.AsNoTracking()
                .Where(a => a.RetrospectiveReviewRequired && a.RetrospectiveReviewed == wantClosed);
            var items = await (wantClosed
                    ? q.OrderByDescending(a => a.RetrospectiveReviewedAt)
                    : q.OrderBy(a => a.DecidedAt))          // oldest first: a backlog is worked from its tail
                .Take(200).ToListAsync(ct);
            return Results.Ok(items.Select(a => RetrospectiveItemView.From(a, now)));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:read"))
        .Produces<IEnumerable<RetrospectiveItemView>>();

        // COMPLETE A RETROSPECTIVE REVIEW.
        //
        // The queue above has existed since 7.3 and nothing could ever empty it: `RetrospectiveReviewed`
        // appeared in exactly two places in the repository — its declaration, and the `!` predicate that reads
        // it. No endpoint, service or job assigned it. So the flag recorded that a review was OWED and never
        // that one happened, and the trail could not tell "reviewed and upheld" apart from "nobody looked".
        //
        // That is not a missing feature; it is the control that makes break-glass defensible. An override is
        // acceptable BECAUSE somebody checks it afterwards.
        v1.MapPost("/{id:guid}/retrospective-review", async (
            Guid id, RetrospectiveReviewRequest req, HttpRequest http,
            ApprovalsDbContext db, ApprovalsGate gate, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.Retrospective, id.ToString(), "retrospective", ct);
            if (denied is not null) return denied;

            if (req.Outcome is not ("Upheld" or "NotJustified"))
                return Results.Problem(statusCode: 422, title: "bad-outcome", type: "urn:hbmp:validation",
                    detail: "The outcome must be Upheld or NotJustified.");
            // A review that records no reasoning is not a review, it is a checkbox — and a checkbox is what
            // this control already effectively was.
            if (DecisionRules.IsBlank(req.Rationale))
                return Results.Problem(statusCode: 422, title: "rationale-required", type: "urn:hbmp:validation",
                    detail: "A retrospective review requires a written rationale.");

            var auth = await db.Authorizations.Include(a => a.Decisions)
                .FirstOrDefaultAsync(a => a.AuthorizationId == id, ct);
            if (auth is null)
                return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (!auth.RetrospectiveReviewRequired)
                return Results.Problem(statusCode: 409, title: "no-review-required", type: "urn:hbmp:conflict",
                    detail: "This authorization was not decided under break-glass, so there is nothing to review.");
            if (auth.RetrospectiveReviewed)
                return Results.Problem(statusCode: 409, title: "already-reviewed", type: "urn:hbmp:conflict",
                    detail: "This break-glass decision has already been reviewed.");

            // SEGREGATION OF DUTIES, per person. Somebody signing off their own override is the precise failure
            // this control exists to catch, and the role split in ApprovalsPolicies does not cover it: a
            // director reviewing another director's override is fine, reviewing their own is not.
            var reviewer = me.Principal?.Subject;
            var actor = auth.Decisions.Where(d => d.BreakGlass).OrderByDescending(d => d.DecidedAt)
                .Select(d => d.ReviewerId).FirstOrDefault(rid => rid is not null);
            if (actor is { } a2 && reviewer is not null && string.Equals(a2.ToString(), reviewer, StringComparison.OrdinalIgnoreCase))
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "authorization", EntityId = id.ToString(), Action = AuditAction.Decision,
                    ActorUserId = reviewer, TenantId = me.Principal?.TenantId,
                    DecisionOutcome = "Deny", DecisionReasonCode = "SOD_SELF_RETROSPECTIVE_REVIEW",
                    BreakGlass = true, Severity = AuditSeverity.High,
                }, ct);
                return Results.Problem(statusCode: 403, title: "segregation-of-duties", type: "urn:hbmp:sod-violation",
                    detail: "You took this break-glass decision. A second, distinct reviewer is required.",
                    extensions: new Dictionary<string, object?> { ["reason"] = "SOD_SELF_RETROSPECTIVE_REVIEW" });
            }

            var now = clock.GetUtcNow();
            auth.RetrospectiveReviewed = true;
            auth.RetrospectiveReviewedBy = reviewer;
            auth.RetrospectiveReviewedAt = now;
            auth.RetrospectiveOutcome = req.Outcome;
            auth.RetrospectiveRationale = req.Rationale!.Trim();
            auth.UpdatedAt = now;

            // NOTE WHAT DOES NOT HAPPEN HERE: `NotJustified` does not touch `auth.Status`. The care was
            // delivered under this authorization; unwinding it retroactively would refuse a service that has
            // already happened, to a beneficiary who had no part in the decision. The finding is the output.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("AuthRetrospectivelyReviewed", "approvals.events",
                new
                {
                    tenantId = auth.TenantId,
                    authorizationId = auth.AuthorizationId, auth.AuthNo,
                    outcome = auth.RetrospectiveOutcome, reviewedAt = now,
                }, ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = id.ToString(), Action = AuditAction.Decision,
                ActorUserId = reviewer, ActorRole = string.Join(',', me.Principal?.Roles ?? new HashSet<string>()),
                TenantId = me.Principal?.TenantId, DecisionOutcome = $"RetrospectiveReview:{req.Outcome}",
                DecisionReasonCode = auth.RetrospectiveRationale, BreakGlass = true, Severity = AuditSeverity.High,
            }, ct);
            await tx.CommitAsync(ct);

            return Results.Ok(RetrospectiveItemView.From(auth, now));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:retrospective"))
        .Produces<RetrospectiveItemView>();

        // TAT / SLA AGGREGATE for the reporting read-model (phase 8). Count by status + avg/p95 TAT + breach count.
        v1.MapGet("/tat-summary", async (ApprovalsDbContext db, ApprovalsGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.List, null, "reporting", ct);
            if (denied is not null) return denied;
            return Results.Ok(await TatReporting.SummaryAsync(db, ct));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:read"))
        .Produces<TatSummary>();
    }
}
