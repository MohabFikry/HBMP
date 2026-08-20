using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.CallCentre.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Api;

/// <summary>Phase 15.1 — call interactions + caller verification (the contact-centre foundation). Every verification
/// attempt (pass AND fail) is persisted and audited; a Pass with ≥ the minimum identifier TYPES binds the
/// interaction to the beneficiary and unlocks the verification gate for 15.2–15.4. Only identifier TYPES are ever
/// stored — never the values the caller recited.</summary>
public static class Interactions
{
    public static void MapInteractions(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/call-interactions");

        // --- Open an interaction ----------------------------------------------------------------------------
        v1.MapPost("", async (OpenInteractionRequest req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Interaction, "open-interaction", ct);
            if (denied is not null) return denied;
            if (!Guid.TryParse(deps.Subject, out var agent))
                return Unprocessable("agent-required", "A valid agent identity is required.");

            var now = deps.Clock.GetUtcNow();
            var i = new CallInteraction
            {
                InteractionId = Guid.NewGuid(),
                CallRef = await deps.CallRef.NextAsync(now.Year, ct),
                TenantId = deps.Tenant ?? "unknown",
                AgentUserId = agent,
                Direction = req.Direction,
                ReasonCode = req.ReasonCode,
                Status = InteractionStatus.Open,
                StartedAt = now,
                CreatedBy = deps.Subject,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            deps.Db.Interactions.Add(i);
            await deps.Db.SaveChangesAsync(ct);
            await deps.Outbox.EnqueueAsync("CallInteractionOpened", "callcentre.events",
                new { interactionId = i.InteractionId, i.CallRef, tenantId = i.TenantId, direction = i.Direction.ToString() }, ct);
            await tx.CommitAsync(ct);
            await deps.AuditAsync("call_interaction", i.InteractionId.ToString(), AuditAction.Create,
                "CallInteractionOpened", i.CallRef, after: i.Status.ToString());
            return Results.Created($"/api/v1/call-interactions/{i.InteractionId}", InteractionView.From(i, false));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"))
        .Produces<InteractionView>();

        // --- Record the agent's caller-identity attestation --------------------------------------------------
        //
        // Identity is confirmed ON THE PHONE. This endpoint records that the agent did so, and BINDS the call to
        // the member — which is the part that still carries weight: it is what stops a call disclosing a member
        // it was never opened against, and what ties every subsequent PHI read to a specific call in the audit
        // trail. There is no threshold to meet and nothing to fail, so there is no 422 and no lockout.
        v1.MapPost("/{id:guid}/verification", async (Guid id, RecordVerificationRequest req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Verify, "record-verification", ct);
            if (denied is not null) return denied;
            if (req.BeneficiaryId == Guid.Empty) return Unprocessable("beneficiary-required", "A beneficiary id is required.");

            var i = await deps.Db.Interactions.FirstOrDefaultAsync(x => x.InteractionId == id, ct);
            if (i is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (i.Status != InteractionStatus.Open)
                return Conflict("This interaction is closed; open a new call to work on a member.");

            var now = deps.Clock.GetUtcNow();
            var v = new CallerVerification
            {
                VerificationId = Guid.NewGuid(),
                InteractionId = id,
                BeneficiaryId = req.BeneficiaryId,
                TenantId = deps.Tenant ?? "unknown",
                // Empty ON PURPOSE. The agent confirmed identity verbally and does not report which identifiers
                // they asked for; writing a plausible set here would be inventing evidence.
                VerifiedIdentifierTypes = [],
                Result = VerificationResult.Passed,
                Method = VerificationMethod.OffSystem,
                VerifiedAt = now,
                VerifiedBy = deps.Subject,
            };
            deps.Db.Verifications.Add(v);

            i.BeneficiaryId = req.BeneficiaryId;
            i.UpdatedAt = now;

            // The binding and the event recording it commit together.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            await deps.Db.SaveChangesAsync(ct);

            await deps.Outbox.EnqueueAsync("CallerVerificationRecorded", "callcentre.events",
                new { interactionId = id, i.CallRef, beneficiaryId = req.BeneficiaryId, result = "Passed", method = "OffSystem" }, ct);
            await tx.CommitAsync(ct);
            await deps.AuditAsync("caller_verification", v.VerificationId.ToString(), AuditAction.Decision,
                "CallerIdentityAttested", i.CallRef, severity: AuditSeverity.Notice,
                after: "method:OffSystem", fieldClasses: ["identity"]);
            return Results.Ok(VerificationView.From(v));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:verify"))
        .Produces<VerificationView>();

        // --- Update the call log (reason/outcome/summary) ---------------------------------------------------
        v1.MapPatch("/{id:guid}", async (Guid id, UpdateInteractionRequest req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Interaction, "update-interaction", ct);
            if (denied is not null) return denied;
            var i = await deps.Db.Interactions.FirstOrDefaultAsync(x => x.InteractionId == id, ct);
            if (i is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (NotOwner(deps, i) is { } notMine) return notMine;
            if (i.Status != InteractionStatus.Open) return Conflict("This interaction is already closed.");

            if (req.ReasonCode is not null) i.ReasonCode = req.ReasonCode;
            if (req.Outcome is not null) i.Outcome = req.Outcome;
            if (req.Summary is not null)
            {
                var draft = req.Summary.Trim();
                if (draft.Length > CallSummaryRules.MaxLength)
                    return Unprocessable("summary-too-long",
                        $"A call summary is capped at {CallSummaryRules.MaxLength} characters; got {draft.Length}.");
                i.Summary = draft;
            }
            i.UpdatedAt = deps.Clock.GetUtcNow();
            try { await deps.Db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict("This interaction was updated by someone else."); }
            await deps.AuditAsync("call_interaction", id.ToString(), AuditAction.Update, "CallInteractionUpdated", i.CallRef);
            var verified = i.BeneficiaryId is { } b && await deps.Verification.IsVerifiedAsync(id, b, ct);
            return Results.Ok(InteractionView.From(i, verified));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"))
        .Produces<InteractionView>();

        // --- Close the interaction (wrap-up; verification expires) ------------------------------------------
        v1.MapPost("/{id:guid}/close", async (Guid id, UpdateInteractionRequest? req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Interaction, "close-interaction", ct);
            if (denied is not null) return denied;
            var i = await deps.Db.Interactions.FirstOrDefaultAsync(x => x.InteractionId == id, ct);
            if (i is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (NotOwner(deps, i) is { } notMine) return notMine;
            if (i.Status == InteractionStatus.Closed) return Results.Ok(InteractionView.From(i, false));

            var now = deps.Clock.GetUtcNow();
            if (req?.ReasonCode is not null) i.ReasonCode = req.ReasonCode;
            if (req?.Outcome is not null) i.Outcome = req.Outcome;
            if (req?.Summary is not null)
            {
                var draft = req.Summary.Trim();
                if (draft.Length > CallSummaryRules.MaxLength)
                    return Unprocessable("summary-too-long",
                        $"A call summary is capped at {CallSummaryRules.MaxLength} characters; got {draft.Length}.");
                i.Summary = draft;
            }

            // Phase 20.3b — a summary is REQUIRED at close unless the call was abandoned. Other roles read this
            // field through the patient profile; a call that closed "Resolved" with nothing recorded leaves a
            // coordinator reading a row that says something happened and refuses to say what.
            if (CallSummaryRules.Validate(i.Outcome, i.Summary) is { } problem)
                return Unprocessable("summary-required", problem);

            i.Status = InteractionStatus.Closed;
            i.EndedAt = now;
            i.UpdatedAt = now;
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            await deps.Db.SaveChangesAsync(ct);
            await deps.Outbox.EnqueueAsync("CallInteractionClosed", "callcentre.events",
                new { interactionId = id, i.CallRef, outcome = i.Outcome?.ToString() }, ct);
            await tx.CommitAsync(ct);
            await deps.AuditAsync("call_interaction", id.ToString(), AuditAction.StateChange, "CallInteractionClosed",
                i.CallRef, after: i.Outcome?.ToString());
            // Once closed the verification gate returns false → member detail is no longer disclosable on this call.
            return Results.Ok(InteractionView.From(i, false));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"))
        .Produces<InteractionView>();

        // --- Correct the summary (phase 20.3b) — an EDIT WITH HISTORY, never a silent overwrite -------------
        // Available after close, unlike the rest of the call log: the summary is the one field other roles rely
        // on, so a genuine correction must be possible — and must be visible as a correction.
        v1.MapPatch("/{id:guid}/summary", async (Guid id, UpdateSummaryRequest req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Interaction, "edit-summary", ct);
            if (denied is not null) return denied;

            var i = await deps.Db.Interactions.FirstOrDefaultAsync(x => x.InteractionId == id, ct);
            if (i is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            if (NotOwner(deps, i) is { } notMine) return notMine;

            var next = req.Summary?.Trim();
            if (string.IsNullOrWhiteSpace(next))
                return Unprocessable("summary-required", "A summary correction cannot be empty — a summary other roles rely on must not be blankable.");
            if (next.Length > CallSummaryRules.MaxLength)
                return Unprocessable("summary-too-long", $"A call summary is capped at {CallSummaryRules.MaxLength} characters; got {next.Length}.");
            if (string.Equals(next, i.Summary, StringComparison.Ordinal))
                return Results.Ok(InteractionView.From(i, false));

            var now = deps.Clock.GetUtcNow();
            deps.Db.SummaryRevisions.Add(new CallSummaryRevision
            {
                RevisionId = Guid.NewGuid(),
                InteractionId = id,
                TenantId = deps.Tenant ?? "unknown",
                PreviousValue = i.Summary,
                NewValue = next,
                EditedBy = deps.Subject,
                EditedAt = now,
            });
            i.Summary = next;
            i.SummaryEditedAt = now;
            i.SummaryEditedBy = deps.Subject;
            i.UpdatedAt = now;
            await deps.Db.SaveChangesAsync(ct);

            await deps.AuditAsync("call_interaction", id.ToString(), AuditAction.Update, "CallSummaryEdited",
                i.CallRef, severity: AuditSeverity.Notice, before: "(previous summary retained in history)",
                after: "edited");
            return Results.Ok(InteractionView.From(i, false));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"))
        .Produces<InteractionView>();

        // --- List interactions (agent sees own; supervisor sees the team) — cursor paged --------------------
        v1.MapGet("", async (CallDeps deps, CancellationToken ct,
            Guid? beneficiaryId, Guid? agentUserId, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? pageSize) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Interaction, "list-interactions", ct);
            if (denied is not null) return denied;

            var p = deps.Me.Principal;
            var isSupervisor = p is not null && (p.IsInRole("call_center_supervisor") || p.IsInRole("manager"));
            var take = Math.Clamp(pageSize ?? 25, 1, 100);

            var q = deps.Db.Interactions.AsNoTracking().Where(x => x.TenantId == deps.Tenant);
            // An agent may only see their OWN calls; a supervisor may filter the team (or all).
            if (!isSupervisor && Guid.TryParse(deps.Subject, out var self))
                q = q.Where(x => x.AgentUserId == self);
            else if (agentUserId is { } au)
                q = q.Where(x => x.AgentUserId == au);

            if (beneficiaryId is { } b) q = q.Where(x => x.BeneficiaryId == b);
            if (from is { } f) q = q.Where(x => x.StartedAt >= f);
            if (to is { } t) q = q.Where(x => x.StartedAt <= t);
            // Cursor over the COMPOSITE (StartedAt, InteractionId), because StartedAt is not unique: two calls
            // opened in the same tick made `StartedAt < cursor.StartedAt` skip every one of them but the first,
            // silently dropping calls out of the middle of a supervisor's page. The tie-break has to be part of
            // both the ordering and the predicate or the two disagree about what "after" means.
            if (Guid.TryParse(cursor, out var after))
            {
                var afterRow = await deps.Db.Interactions.AsNoTracking().FirstOrDefaultAsync(x => x.InteractionId == after, ct);
                if (afterRow is not null)
                    q = q.Where(x => x.StartedAt < afterRow.StartedAt
                                     || (x.StartedAt == afterRow.StartedAt && x.InteractionId.CompareTo(afterRow.InteractionId) > 0));
            }

            var rows = await q.OrderByDescending(x => x.StartedAt).ThenBy(x => x.InteractionId).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var next = rows.Count > take ? page[^1].InteractionId.ToString() : null;
            return Results.Ok(new InteractionListResponse(
                page.Select(x => InteractionView.From(x, false)).ToList(), next));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"))
        .Produces<IEnumerable<InteractionView>>();
    }

    /// <summary>Whether this principal may WRITE to this interaction: the agent who took the call, or a
    /// supervisor/manager. Returns a ready 403 when not, else null.
    ///
    /// <para>The policy layer cannot answer this — <c>CallCentrePolicies</c> is role + tenant only, by design
    /// (the Call Centre is MemberScoped, so there is no branch or per-record ABAC in the engine). The GET list
    /// below has always narrowed a non-supervisor to their own calls; the write paths did not, so any
    /// <c>call_center</c> holder could patch, close, or rewrite the summary on any colleague's call in the
    /// tenant. The summary is the field other roles read, which made that the most consequential of the three.
    /// The rule the policy doc already stated ("the agent's own calls") is now enforced where it is decided.</para></summary>
    private static IResult? NotOwner(CallDeps deps, CallInteraction i)
    {
        var p = deps.Me.Principal;
        if (p is null) return GateResults.Unauthenticated();
        if (p.IsInRole("call_center_supervisor") || p.IsInRole("manager")) return null;
        if (Guid.TryParse(deps.Subject, out var self) && i.AgentUserId == self) return null;

        return GateResults.Forbidden("urn:hbmp:callcentre-not-your-call",
            detail: "This call was taken by another agent. Only that agent or a supervisor may change its record.",
            reason: "not-call-owner");
    }

    private static IResult Unprocessable(string title, string detail) =>
        Results.Problem(statusCode: 422, title: title, detail: detail, type: $"urn:hbmp:{title}");

    private static IResult Conflict(string detail) =>
        Results.Problem(statusCode: 409, title: "conflict", detail: detail, type: "urn:hbmp:conflict");
}
