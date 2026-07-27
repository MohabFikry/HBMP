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
            deps.Db.Interactions.Add(i);
            await deps.Db.SaveChangesAsync(ct);
            await deps.Outbox.EnqueueAsync("CallInteractionOpened", "callcentre.events",
                new { interactionId = i.InteractionId, i.CallRef, tenantId = i.TenantId, direction = i.Direction.ToString() }, ct);
            await deps.AuditAsync("call_interaction", i.InteractionId.ToString(), AuditAction.Create,
                "CallInteractionOpened", i.CallRef, after: i.Status.ToString());
            return Results.Created($"/api/v1/call-interactions/{i.InteractionId}", InteractionView.From(i, false));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"));

        // --- Record a caller-verification attempt (pass or fail) --------------------------------------------
        v1.MapPost("/{id:guid}/verification", async (Guid id, RecordVerificationRequest req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Verify, "record-verification", ct);
            if (denied is not null) return denied;
            if (req.BeneficiaryId == Guid.Empty) return Unprocessable("beneficiary-required", "A beneficiary id is required.");

            var i = await deps.Db.Interactions.FirstOrDefaultAsync(x => x.InteractionId == id, ct);
            if (i is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (i.Status != InteractionStatus.Open)
                return Conflict("This interaction is closed; open a new call to verify.");

            var types = VerificationPolicy.Normalise(req.VerifiedIdentifierTypes);
            var now = deps.Clock.GetUtcNow();

            // A Pass demands at least the minimum DISTINCT known identifier types; otherwise it's rejected (422) but
            // NOT recorded as a spurious Pass — the agent must challenge on more identifiers.
            if (req.Result == VerificationResult.Passed && !VerificationPolicy.MeetsThreshold(types))
                return Unprocessable("insufficient-identifiers",
                    $"A pass requires at least {VerificationPolicy.MinIdentifierTypes} confirmed identifier types; got {types.Count}.");

            var effectiveResult = req.Result;
            var v = new CallerVerification
            {
                VerificationId = Guid.NewGuid(),
                InteractionId = id,
                BeneficiaryId = req.BeneficiaryId,
                TenantId = deps.Tenant ?? "unknown",
                VerifiedIdentifierTypes = types.ToList(),
                Result = effectiveResult,
                FailureReason = effectiveResult == VerificationResult.Failed
                    ? (string.IsNullOrWhiteSpace(req.FailureReason) ? "unconfirmed" : req.FailureReason[..Math.Min(64, req.FailureReason.Length)])
                    : null,
                VerifiedAt = now,
                VerifiedBy = deps.Subject,
            };
            deps.Db.Verifications.Add(v);

            // A Pass binds the interaction to this beneficiary — the anchor the verification gate consults.
            if (effectiveResult == VerificationResult.Passed)
            {
                i.BeneficiaryId = req.BeneficiaryId;
                i.UpdatedAt = now;
            }
            await deps.Db.SaveChangesAsync(ct);

            await deps.Outbox.EnqueueAsync("CallerVerificationRecorded", "callcentre.events",
                new { interactionId = id, i.CallRef, beneficiaryId = req.BeneficiaryId, result = effectiveResult.ToString(), typeCount = types.Count }, ct);
            // Both passes and failures are audited. A failure is a Notice (a disclosure was withheld / attempted).
            await deps.AuditAsync("caller_verification", v.VerificationId.ToString(), AuditAction.Decision,
                effectiveResult == VerificationResult.Passed ? "CallerVerificationPassed" : "CallerVerificationFailed",
                i.CallRef,
                severity: effectiveResult == VerificationResult.Passed ? AuditSeverity.Notice : AuditSeverity.Warning,
                after: $"types:{types.Count}",
                fieldClasses: ["identity"]);
            return Results.Ok(VerificationView.From(v));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:verify"));

        // --- Update the call log (reason/outcome/notes) -----------------------------------------------------
        v1.MapPatch("/{id:guid}", async (Guid id, UpdateInteractionRequest req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Interaction, "update-interaction", ct);
            if (denied is not null) return denied;
            var i = await deps.Db.Interactions.FirstOrDefaultAsync(x => x.InteractionId == id, ct);
            if (i is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (i.Status != InteractionStatus.Open) return Conflict("This interaction is already closed.");

            if (req.ReasonCode is not null) i.ReasonCode = req.ReasonCode;
            if (req.Outcome is not null) i.Outcome = req.Outcome;
            if (req.Notes is not null) i.Notes = req.Notes;
            i.UpdatedAt = deps.Clock.GetUtcNow();
            try { await deps.Db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict("This interaction was updated by someone else."); }
            await deps.AuditAsync("call_interaction", id.ToString(), AuditAction.Update, "CallInteractionUpdated", i.CallRef);
            var verified = i.BeneficiaryId is { } b && await deps.Verification.IsVerifiedAsync(id, b, ct);
            return Results.Ok(InteractionView.From(i, verified));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"));

        // --- Close the interaction (wrap-up; verification expires) ------------------------------------------
        v1.MapPost("/{id:guid}/close", async (Guid id, UpdateInteractionRequest? req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Interaction, "close-interaction", ct);
            if (denied is not null) return denied;
            var i = await deps.Db.Interactions.FirstOrDefaultAsync(x => x.InteractionId == id, ct);
            if (i is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (i.Status == InteractionStatus.Closed) return Results.Ok(InteractionView.From(i, false));

            var now = deps.Clock.GetUtcNow();
            if (req?.ReasonCode is not null) i.ReasonCode = req.ReasonCode;
            if (req?.Outcome is not null) i.Outcome = req.Outcome;
            if (req?.Notes is not null) i.Notes = req.Notes;
            if (req?.Summary is not null) i.Summary = req.Summary.Trim();

            // Phase 20.3b — a summary is REQUIRED at close unless the call was abandoned. Other roles read this
            // field through the patient profile; a call that closed "Resolved" with nothing recorded leaves a
            // coordinator reading a row that says something happened and refuses to say what.
            if (CallSummaryRules.Validate(i.Outcome, i.Summary) is { } problem)
                return Unprocessable("summary-required", problem);

            i.Status = InteractionStatus.Closed;
            i.EndedAt = now;
            i.UpdatedAt = now;
            await deps.Db.SaveChangesAsync(ct);
            await deps.Outbox.EnqueueAsync("CallInteractionClosed", "callcentre.events",
                new { interactionId = id, i.CallRef, outcome = i.Outcome?.ToString() }, ct);
            await deps.AuditAsync("call_interaction", id.ToString(), AuditAction.StateChange, "CallInteractionClosed",
                i.CallRef, after: i.Outcome?.ToString());
            // Once closed the verification gate returns false → member detail is no longer disclosable on this call.
            return Results.Ok(InteractionView.From(i, false));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"));

        // --- Correct the summary (phase 20.3b) — an EDIT WITH HISTORY, never a silent overwrite -------------
        // Available after close, unlike the rest of the call log: the summary is the one field other roles rely
        // on, so a genuine correction must be possible — and must be visible as a correction.
        v1.MapPatch("/{id:guid}/summary", async (Guid id, UpdateSummaryRequest req, CallDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Interaction, "edit-summary", ct);
            if (denied is not null) return denied;

            var i = await deps.Db.Interactions.FirstOrDefaultAsync(x => x.InteractionId == id, ct);
            if (i is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

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
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"));

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
            if (Guid.TryParse(cursor, out var after))
            {
                var afterRow = await deps.Db.Interactions.AsNoTracking().FirstOrDefaultAsync(x => x.InteractionId == after, ct);
                if (afterRow is not null) q = q.Where(x => x.StartedAt < afterRow.StartedAt);
            }

            var rows = await q.OrderByDescending(x => x.StartedAt).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var next = rows.Count > take ? page[^1].InteractionId.ToString() : null;
            return Results.Ok(new InteractionListResponse(
                page.Select(x => InteractionView.From(x, false)).ToList(), next));
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:interaction"));
    }

    private static IResult Unprocessable(string title, string detail) =>
        Results.Problem(statusCode: 422, title: title, detail: detail, type: $"urn:hbmp:{title}");

    private static IResult Conflict(string detail) =>
        Results.Problem(statusCode: 409, title: "conflict", detail: detail, type: "urn:hbmp:conflict");
}
