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
            if (auth is null) return Results.NotFound();

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
            if (auth is null) return Results.NotFound();
            if (!AuthorizationWorkflow.CanTransition(auth.Status, AuthStatus.UnderReview))
                return IllegalTransition(auth.Status);

            var before = auth.Status;
            auth.Status = AuthStatus.UnderReview;
            auth.UpdatedAt = clock.GetUtcNow();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            await outbox.EnqueueAsync("AuthInfoSupplied", "approvals.events",
                new { authorizationId = auth.AuthorizationId, auth.AuthNo }, ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                BeforeState = before.ToString(), AfterState = auth.Status.ToString(), DecisionOutcome = "InfoSupplied",
            }, ct);
            return Results.Ok(AuthorizationStateView.From(auth));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:decide"));
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

        // Idempotent replay: a repeated key returns the prior recorded decision, no new row / state.
        var prior = await deps.Db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
        if (prior is not null)
        {
            var a0 = await deps.Db.Authorizations.AsNoTracking().Include(a => a.Decisions)
                .FirstOrDefaultAsync(a => a.AuthorizationId == prior.AuthorizationId, ct);
            if (a0 is null) return Results.NoContent();
            var d0 = a0.Decisions.OrderByDescending(d => d.DecidedAt).First();
            return Results.Ok(DecisionView.From(a0, d0));
        }

        var auth = await deps.Db.Authorizations.FirstOrDefaultAsync(a => a.AuthorizationId == id, ct);
        if (auth is null) return Results.NotFound();

        var target = AuthorizationWorkflow.ResultOf(decision);
        if (!AuthorizationWorkflow.CanTransition(auth.Status, target))
            return IllegalTransition(auth.Status);

        var now = deps.Clock.GetUtcNow();
        var reviewerId = Guid.TryParse(deps.Me.Principal?.Subject, out var rg) ? rg : Guid.Empty;
        var before = auth.Status;

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
        });
        await deps.Db.SaveChangesAsync(ct);

        await deps.Outbox.EnqueueAsync(EventType(decision), "approvals.events", new
        {
            authorizationId = auth.AuthorizationId, auth.AuthNo, beneficiaryId = auth.BeneficiaryId,
            source = auth.Source.ToString(), sourceRef = auth.SourceRef,
            approvedScope = approvedScopeJson is null ? null : Codes.Parse(approvedScopeJson),
            releasesDownstream = AuthorizationWorkflow.ReleasesDownstream(decision), breakGlass,
        }, ct);
        await tx.CommitAsync(ct);

        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.Decision,
            ActorUserId = deps.Me.Principal?.Subject, ActorRole = string.Join(',', deps.Me.Principal?.Roles ?? new HashSet<string>()),
            TenantId = deps.Me.Principal?.TenantId, BeforeState = before.ToString(), AfterState = target.ToString(),
            DecisionOutcome = decision.ToString(), DecisionReasonCode = rationale, BreakGlass = breakGlass,
            Severity = breakGlass ? AuditSeverity.High : AuditSeverity.Notice,
        }, ct);

        return Results.Ok(DecisionView.From(auth, row));
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
    IHbmpPrincipalAccessor me, TimeProvider clock)
{
    public ApprovalsDbContext Db { get; } = db;
    public ApprovalsGate Gate { get; } = gate;
    public IAuditClient Audit { get; } = audit;
    public IOutbox Outbox { get; } = outbox;
    public IHbmpPrincipalAccessor Me { get; } = me;
    public TimeProvider Clock { get; } = clock;
}
