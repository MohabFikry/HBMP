using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>Phase 7.2 — decisions with mandatory rationale (US-060). Every decision writes an APPEND-ONLY
/// authorization_decision row + drives the state machine + emits the canonical event, all in ONE transaction; TAT
/// is captured and SLA-breach flagged. approve / partially-approve / reject / request-info transition from
/// UnderReview; resupply moves InfoRequested → UnderReview. Rejection reason is mandatory (422); partial approval
/// must carry a non-empty strict-subset approved_scope. Two reviewers deciding the same case race on xmin → one
/// wins, the other gets 409. Downstream: Approved/Partial release the linked order/prescription gate (consumers of
/// the emitted event dedupe on event id); Rejected blocks.</summary>
public static class Decisions
{
    public static void MapDecisions(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/authorizations");

        v1.MapPost("/{id:guid}/approve", async (Guid id, ApproveRequest req, HttpRequest http, DecisionDeps deps, CancellationToken ct) =>
            await Decide(id, AuthDecision.Approved, req.Rationale, null, http, deps, ct))
            .RequireAuthorization(HbmpPolicies.Scope("auth:decide"));

        v1.MapPost("/{id:guid}/partially-approve", async (Guid id, PartialApproveRequest req, HttpRequest http, DecisionDeps deps, CancellationToken ct) =>
        {
            if (DecisionRules.IsBlank(req.Rationale))
                return Unprocessable("rationale-required", "A rationale is required for a partial approval.");

            var auth = await deps.Db.Authorizations.AsNoTracking().FirstOrDefaultAsync(a => a.AuthorizationId == id, ct);
            if (auth is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var err = DecisionRules.ValidatePartialScope(Codes.Parse(auth.ServiceCodes), req.ApprovedScope ?? []);
            if (err != PartialScopeError.None)
                return Unprocessable("invalid-approved-scope",
                    err switch
                    {
                        PartialScopeError.Empty => "approved_scope must not be empty.",
                        PartialScopeError.NotSubset => "approved_scope must be a subset of the requested codes.",
                        _ => "approved_scope equals the full request — use approve, not partially-approve.",
                    });

            return await Decide(id, AuthDecision.PartiallyApproved, req.Rationale, Codes.Serialize(req.ApprovedScope!), http, deps, ct);
        }).RequireAuthorization(HbmpPolicies.Scope("auth:decide"));

        v1.MapPost("/{id:guid}/reject", async (Guid id, RejectRequest req, HttpRequest http, DecisionDeps deps, CancellationToken ct) =>
        {
            // Rejection reason is MANDATORY (23 §5, 19-audit-strategy).
            if (DecisionRules.IsBlank(req.Rationale))
                return Unprocessable("rejection-reason-required", "A rejection reason (rationale) is mandatory.");
            return await Decide(id, AuthDecision.Rejected, req.Rationale, null, http, deps, ct);
        }).RequireAuthorization(HbmpPolicies.Scope("auth:decide"));

        v1.MapPost("/{id:guid}/request-info", async (Guid id, RequestInfoRequest req, HttpRequest http, DecisionDeps deps, CancellationToken ct) =>
        {
            if (DecisionRules.IsBlank(req.Rationale))
                return Unprocessable("missing-info-required", "State what information is missing (rationale is mandatory).");
            return await Decide(id, AuthDecision.InfoRequested, req.Rationale, null, http, deps, ct);
        }).RequireAuthorization(HbmpPolicies.Scope("auth:decide"));

        // Resupply: the requester supplies the missing info, reopening review (InfoRequested → UnderReview). This is
        // a state change, not a decision (no ledger row); it emits AuthInfoSupplied.
        v1.MapPost("/{id:guid}/resupply", async (
            Guid id, HttpRequest http, ApprovalsDbContext db, ApprovalsGate gate,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(ApprovalsPolicies.Decide, id.ToString(), "resupply", ct);
            if (denied is not null) return denied;

            var auth = await db.Authorizations.FirstOrDefaultAsync(a => a.AuthorizationId == id, ct);
            if (auth is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (!AuthorizationWorkflow.CanTransition(auth.Status, AuthStatus.UnderReview))
                return IllegalTransition(auth.Status);

            var before = auth.Status;
            auth.Status = AuthStatus.UnderReview;
            auth.UpdatedAt = clock.GetUtcNow();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            await outbox.EnqueueAsync("AuthInfoSupplied", "approvals.events",
                new { tenantId = auth.TenantId, authorizationId = auth.AuthorizationId, auth.AuthNo }, ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                BeforeState = before.ToString(), AfterState = auth.Status.ToString(), DecisionOutcome = "InfoSupplied",
            }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(AuthorizationStateView.From(auth));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:decide"))
        .Produces<AuthorizationStateView>();
    }

    /// <summary>The shared decide path: idempotency → gate → transition guard → append-only decision row + status +
    /// TAT/SLA + canonical event in ONE tx (xmin concurrency) → audit. Used by the standard decisions here and (with
    /// a break-glass flag) by the phase-7.3 emergency/override/manual paths.</summary>
    internal static async Task<IResult> Decide(
        Guid id, AuthDecision decision, string? rationale, string? approvedScopeJson,
        HttpRequest http, DecisionDeps deps, CancellationToken ct,
        bool breakGlass = false, string? justification = null, AuthStatus? fromOverride = null)
    {
        var idem = http.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idem))
            return Results.Problem(statusCode: 400, title: "missing-idempotency-key",
                detail: "An Idempotency-Key header is required.", type: "urn:hbmp:missing-idempotency-key");

        var action = breakGlass
            ? (decision == AuthDecision.EmergencyApproved ? ApprovalsPolicies.Emergency
               : decision == AuthDecision.Overridden ? ApprovalsPolicies.Override : ApprovalsPolicies.Manual)
            : ApprovalsPolicies.Decide;
        var denied = await deps.Gate.CheckAsync(action, id.ToString(), breakGlass ? "break-glass" : "decide", ct);
        if (denied is not null) return denied;

        /*
         * IDEMPOTENT REPLAY, BOUND TO THE BODY.
         *
         * A repeated key returns the prior recorded decision — but only when it is the SAME decision. This
         * compared the key alone until the 2026-08-09 audit, so a reject retried under a key already used for
         * an approve came back "approved", 200 OK: the reviewer is told the opposite of what they asked for,
         * the authorization really is approved, and nothing anywhere records the disagreement. 18.A3 settled
         * the rule for consume and dispense; the approvals ledger simply had no column to apply it with
         * (migration 0011).
         *
         * The hash covers what the DECISION is — which authorization, which verdict, on what rationale and
         * what scope. Not the reviewer: two reviewers cannot share a key, because the second one's decision
         * would be refused by the state machine long before it reached here.
         */
        var requestHash = IdempotencyKeyRules.Hash(
            id.ToString(), decision.ToString(), rationale ?? "", approvedScopeJson ?? "", justification ?? "");

        var prior = await deps.Db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
        if (prior is not null)
        {
            if (!IdempotencyKeyRules.Matches(prior.RequestHash, requestHash))
                return Results.Problem(statusCode: 422, title: "idempotency-key-reuse",
                    type: "urn:hbmp:idempotency-key-reuse",
                    detail: "That key was already used for a different decision. Answering it with the "
                          + "earlier one would report a verdict you did not give.");

            var a0 = await deps.Db.Authorizations.AsNoTracking().Include(a => a.Decisions)
                .FirstOrDefaultAsync(a => a.AuthorizationId == prior.AuthorizationId, ct);
            if (a0 is null) return Results.NoContent();
            var d0 = a0.Decisions.OrderByDescending(d => d.DecidedAt).First();
            return Results.Ok(DecisionView.From(a0, d0));
        }

        var auth = await deps.Db.Authorizations.FirstOrDefaultAsync(a => a.AuthorizationId == id, ct);
        if (auth is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

        var target = AuthorizationWorkflow.ResultOf(decision);
        if (!AuthorizationWorkflow.CanTransition(auth.Status, target))
            return IllegalTransition(auth.Status);

        var now = deps.Clock.GetUtcNow();
        var reviewerId = Guid.TryParse(deps.Me.Principal?.Subject, out var rg) ? rg : Guid.Empty;
        var before = auth.Status;

        /*
         * A VALIDITY EXTENSION IS APPLIED BEFORE IT IS RECORDED.
         *
         * approvals owns the decision; pharmacy and orders own the thing decided about, and only they can
         * move its expiry. So an approval has to travel — and the ORDER matters more than it looks.
         *
         * Recording first and calling after leaves an authorization that says Approved beside a prescription
         * the counter still cannot dispense: the pharmacist is told yes by one screen and no by the next,
         * with nothing on either to explain the disagreement. Doing it this way, the reviewer gets both or
         * neither, and a failure is a 502 they can see and retry rather than a silent split.
         *
         * Nothing is refused for a REJECTION — there is nothing to apply, and a rejection must land even if
         * pharmacy is unreachable.
         */
        DateTimeOffset? extendedTo = null;
        if (auth.Source == AuthSource.ValidityExtension && AuthorizationWorkflow.ReleasesDownstream(decision))
        {
            var outcome = await deps.Extensions.ApplyAsync(auth, http.Headers.Authorization.ToString(), ct);
            if (!outcome.Applied)
                return Results.Problem(
                    statusCode: 502, title: "extension-not-applied", type: "urn:hbmp:extension-not-applied",
                    detail: outcome.Failure + " The decision has NOT been recorded — nothing has changed, and "
                            + "this can be retried.");
            extendedTo = outcome.NewExpiry;
        }

        var row = new AuthorizationDecision
        {
            DecisionId = Guid.NewGuid(), AuthorizationId = auth.AuthorizationId, Decision = decision,
            ReviewerId = reviewerId, DecidedAt = now, Rationale = rationale, ApprovedScope = approvedScopeJson,
            BreakGlass = breakGlass, Justification = justification,
            CorrelationId = http.HttpContext?.TraceIdentifier,
        };
        auth.Status = target;
        auth.DecidedAt = now;
        auth.TatSeconds = DecisionRules.TatSeconds(auth.SubmittedAt, now);
        auth.SlaBreached = DecisionRules.SlaBreached(auth.SlaDueAt, now);
        // A break-glass decision (emergency / override / manual) is flagged for post-hoc retrospective review.
        if (breakGlass) auth.RetrospectiveReviewRequired = true;
        auth.UpdatedAt = now;

        await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
        // Update the parent FIRST: this takes the row's exclusive lock and applies the xmin optimistic-concurrency
        // check, so a racing reviewer is rejected here (409) BEFORE we insert the append-only child — inserting the
        // child first would take an FK share lock and, under simultaneous deciders, deadlock on the lock upgrade.
        try { await deps.Db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }   // another reviewer decided first (xmin moved)

        deps.Db.Set<AuthorizationDecision>().Add(row);
        deps.Db.ProcessedRequests.Add(new ProcessedRequest
        {
            IdempotencyKey = idem, Operation = $"decision:{decision}",
            AuthorizationId = auth.AuthorizationId, StatusCode = 200, CreatedAt = now,
            RequestHash = requestHash,
        });
        // The new expiry is part of the DECISION record, not only of the item — "what did approving this
        // actually grant" has to be answerable from the ledger without calling another service.
        if (extendedTo is { } newExpiry && row.ApprovedScope is null)
            row.ApprovedScope = System.Text.Json.JsonSerializer.Serialize(new { extendedTo = newExpiry });
        await deps.Db.SaveChangesAsync(ct);

        var eventType = EventType(decision);
        await deps.Outbox.EnqueueAsync(eventType, "approvals.events", new
        {
            // `tenantId` — emr's care-episode consumer (ADR-0031) binds its RLS session from this envelope and
            // refuses a message it cannot attribute. `encounterId` is what lets the decision land on the right
            // patient's episode; NULL on a manual authorization, and the step is then simply not written.
            tenantId = auth.TenantId,
            authorizationId = auth.AuthorizationId, auth.AuthNo, beneficiaryId = auth.BeneficiaryId,
            encounterId = auth.EncounterId,
            source = auth.Source.ToString(), sourceRef = auth.SourceRef,
            approvedScope = approvedScopeJson is null ? null : Codes.Parse(approvedScopeJson),
            releasesDownstream = AuthorizationWorkflow.ReleasesDownstream(decision), breakGlass,
            /*
             * THE DECISION'S OWN MEASUREMENTS.
             *
             * `auth.TatSeconds` and `auth.SlaBreached` are computed four lines above this and were written to
             * the row and nowhere else, so the approval-TAT report — the whole point of an authorization read
             * model — had no turnaround times and no breach counts to build from. `priority` matters for the
             * same reason: TAT is meaningless unaggregated by it, because Urgent and Routine are answering
             * different promises.
             *
             * `reviewerId` is the one attribution the read model can legitimately hold.
             *
             * `rejectionReason` is deliberately NOT sent, even though `AuthorizationFact` has a column for it.
             * This domain has no reason-code vocabulary — a rejection carries the free-text rationale the
             * reviewer typed, and that is clinical prose which stays on the authorization, behind
             * authorization. Deriving a "code" from it would be inventing a taxonomy at the point of export,
             * and a report that groups by a made-up code is worse than one that admits it cannot group. The
             * column stays null until there is a real coded reason to put in it.
             */
            priority = auth.Priority.ToString(),
            reviewerId,
            auth.TatSeconds,
            auth.SlaBreached,
        }, ct);

        // ── TELL THE PERSON WHO IS WAITING ──────────────────────────────────────────────────────────────────
        //
        // A SECOND, notification-shaped copy on notification-service's own queue. Not a redirect of the line
        // above: `approvals.events` carries the decision to whatever consumes it for projections, and the
        // transport is point-to-point, so a second consumer on that queue would COMPETE for the messages and
        // each event would reach one of them, never both.
        //
        // Until now this did not exist. `RoutingTable` has routed `AuthApproved`, `AuthRejected`,
        // `AuthInfoRequested`, `AuthPartiallyApproved` and `AuthEmergencyApproved` since phase 8.1, with
        // bilingual templates authored for each — and nothing ever delivered one, so a clinician learned their
        // authorization had been decided by opening the worklist and looking.
        //
        // Addressed to the SUBMITTER by subject, which approvals-service is the only service that knows. An
        // authorization with no recorded submitter (a manual one, created out of band) has no addressee, and
        // the consumer drops it rather than broadcasting to a role.
        //
        // ENQUEUED HERE, inside the transaction, rather than inside the helper. The payload construction —
        // which is the part with the judgement in it — stays in `DecisionNotification`; the enqueue does not,
        // because INV-OUTBOX-SURVIVES-CRASH is a property of the CALL SITE and a helper that enqueues on its
        // own is a shape the architecture gate cannot verify and a reader cannot check by eye. It was inside
        // the helper, and it was atomic only because this one caller happened to hold a transaction open.
        if (DecisionNotification(auth) is { } notice)
            await deps.Outbox.EnqueueAsync(eventType, "notification.domain-events", notice, ct);

        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.Decision,
            ActorUserId = deps.Me.Principal?.Subject, ActorRole = string.Join(',', deps.Me.Principal?.Roles ?? new HashSet<string>()),
            TenantId = deps.Me.Principal?.TenantId, BeforeState = before.ToString(), AfterState = target.ToString(),
            DecisionOutcome = decision.ToString(), DecisionReasonCode = rationale, BreakGlass = breakGlass,
            Severity = breakGlass ? AuditSeverity.High : AuditSeverity.Notice,
        }, ct);
        await tx.CommitAsync(ct);

        return Results.Ok(DecisionView.From(auth, row));
    }

    /// <summary>
    /// Build the notification-shaped copy of a decision, or null when there is no addressee.
    ///
    /// <para>The field bag is min-necessary and NON-clinical (11-permission-matrix, and the dispatcher throws
    /// on a forbidden key): the authorization number the clinician recognises, and nothing else. The rationale
    /// the reviewer wrote stays on the authorization, behind authorization.</para>
    ///
    /// <para><b>It builds; it does not publish.</b> The enqueue belongs at the call site, inside the caller's
    /// transaction — see the note there. Splitting it this way keeps the payload judgement in one place while
    /// leaving the atomicity visible where it is decided.</para>
    /// </summary>
    private static object? DecisionNotification(Authorization auth)
    {
        if (string.IsNullOrWhiteSpace(auth.CreatedBy)) return null;
        return new
        {
            tenantId = auth.TenantId,
            entityRef = $"authorization:{auth.AuthorizationId}",
            // `ref`, because that is the token every auth template interpolates ("Authorization {ref} was
            // approved"). Named `authNo` at first, and a missing token renders EMPTY rather than leaking the
            // brace — so the notice went out reading "Authorization  was approved" and nothing failed. The
            // field bag and the template vocabulary are one contract; the template owns the names.
            fields = new { @ref = auth.AuthNo },
            recipients = new[]
            {
                // `requesting_provider` is the role `RoutingTable` targets for every auth decision. The person
                // is the one who submitted it — a role-wide fan-out would put a decision about one clinician's
                // patient in every clinician's inbox.
                new { userId = auth.CreatedBy, role = "requesting_provider", locale = "ar" },
            },
        };
    }

    private static string EventType(AuthDecision d) => d switch
    {
        AuthDecision.Approved => "AuthApproved",
        AuthDecision.PartiallyApproved => "AuthPartiallyApproved",
        AuthDecision.Rejected => "AuthRejected",
        AuthDecision.InfoRequested => "AuthInfoRequested",
        AuthDecision.Overridden => "AuthOverridden",
        AuthDecision.EmergencyApproved => "AuthEmergencyApproved",
        _ => "AuthDecided",
    };

    private static IResult Unprocessable(string title, string detail) =>
        Results.Problem(statusCode: 422, title: title, detail: detail, type: "urn:hbmp:validation");

    private static IResult IllegalTransition(AuthStatus status) =>
        Results.Problem(statusCode: 409, title: "illegal-transition",
            detail: $"No legal decision from status {status}.", type: "urn:hbmp:illegal-transition",
            extensions: new Dictionary<string, object?> { ["status"] = status.ToString() });

    private static IResult Conflict() =>
        Results.Problem(statusCode: 409, title: "already-decided",
            detail: "This request was decided by another reviewer.", type: "urn:hbmp:already-decided");
}

/// <summary>Bundles the decide-path dependencies so the shared helper (and the phase-7.3 break-glass endpoints)
/// take one injected object rather than a long parameter list.</summary>
public sealed class DecisionDeps(
    ApprovalsDbContext db, ApprovalsGate gate, IAuditClient audit, IOutbox outbox,
    IHbmpPrincipalAccessor me, TimeProvider clock, IValidityExtensionApplier extensions)
{
    public IValidityExtensionApplier Extensions { get; } = extensions;
    public ApprovalsDbContext Db { get; } = db;
    public ApprovalsGate Gate { get; } = gate;
    public IAuditClient Audit { get; } = audit;
    public IOutbox Outbox { get; } = outbox;
    public IHbmpPrincipalAccessor Me { get; } = me;
    public TimeProvider Clock { get; } = clock;
}
