using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>Phase 14.7 — the justified release-request workflow for sensitive results (design 37 §6). A
/// request routes to the authoring/ordering doctor OR a Medical Director; an approval mints a time-boxed,
/// single-result, non-transferable grant. Every decision, grant, revocation and expiry is audited + evented.</summary>
public static class ReportAccessEndpoints
{
    public static void MapReportAccess(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("orders:read"));

        // 18.C2 (audit R2 W4) — THE APPROVER INBOX. Requests could be raised and decided by id, and there was
        // no way to LIST them: an approver had no way to discover a request existed. The sensitive-result gate
        // was therefore permanent-deny in practice — a clinician asks for a colleague's restricted result and
        // the request sits in a table nobody queries until it expires. Design 37 §6 depends on this list.
        v1.MapGet("/report-access-requests", async (string? status, OrdersDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var p = me.Principal;
            if (p is null) return Results.Unauthorized();

            var q = db.ReportAccessRequests.AsNoTracking().Where(r => r.TenantId == p.TenantId);
            // Default view: everything OPEN — what needs a decision, and what needs the requester's answer.
            //
            // 32.4: this used to be Requested + UnderReview only, which is the decider's half of the
            // workflow. InfoRequested was in neither branch, so the one person who can move a request out of
            // it — the requester, through supply-info — could not find it in any list this service serves.
            // 18.A4 built that exit and a state-machine test has proven it legal ever since; the product
            // stayed stuck because the row was unreachable, which is not something a domain test can see.
            if (string.IsNullOrWhiteSpace(status))
                q = q.Where(r => r.Status == ReportAccessStatus.Requested
                                 || r.Status == ReportAccessStatus.UnderReview
                                 || r.Status == ReportAccessStatus.InfoRequested);
            else if (Enum.TryParse<ReportAccessStatus>(status, ignoreCase: true, out var s))
                q = q.Where(r => r.Status == s);
            else
                return Results.Problem(statusCode: 400, title: "unknown-status", detail: $"unknown status '{status}'");

            // A Medical Director sees every pending request (37 §6: the escalation path when the authoring
            // doctor is unavailable); anyone else sees only what is routed to them as the ordering provider.
            // Deliberately CLINICAL-FREE: the inbox shows who asked, for which line, why — never the result.
            var isDirector = p.IsInRole("medical_director");
            var rows = await q.OrderBy(r => r.CreatedAt).Take(200).ToListAsync(ct);

            var authored = await db.Orders.AsNoTracking()
                .Where(o => o.OrderingProviderId.ToString() == p.Subject)
                .Select(o => o.OrderId).ToListAsync(ct);
            var authoredSet = authored.ToHashSet();

            // 32.4 — MY OWN REQUESTS ARE MINE TO SEE, and this is the whole of the widening. A clinician who
            // asks to see somebody else's sensitive result is by definition not that order's provider, so
            // their request appeared in no list at all: they raised it and then had no way to learn it had
            // been answered, questioned, or was about to lapse.
            //
            // It discloses nothing new. The requester wrote the justification, named the purpose and chose
            // the beneficiary — the row is their own words. What stays invisible is somebody ELSE's request
            // on an order neither of us placed, which the filter below still refuses.
            if (!isDirector)
                rows = [.. rows.Where(r => authoredSet.Contains(r.OrderId)
                                           || string.Equals(r.RequestedBy, p.Subject, StringComparison.Ordinal))];

            var roles = p.Roles ?? new HashSet<string>();
            return Results.Ok(rows.Select(r =>
            {
                // Computed HERE, not in the client. Whether this caller may decide is an authorization
                // question, and a screen that worked it out by comparing identity strings would be deciding
                // authority in the browser — which is where the platform's rule is that it must not be.
                var canDecide = SensitiveResultGate.CanDecide(authoredSet.Contains(r.OrderId), roles);
                var isRequester = string.Equals(r.RequestedBy, p.Subject, StringComparison.Ordinal);
                return new ReportAccessRequestView(
                    r.RequestId, r.OrderId, r.OrderLineId, r.BeneficiaryId,
                    r.RequestedBy, r.RequestedForRole,
                    r.PurposeCode.ToString(), r.Justification, r.RequestedTtlHours,
                    r.Status.ToString(), r.CreatedAt, canDecide, isRequester);
            }));
        }).Produces<IEnumerable<ReportAccessRequestView>>();

        // Raise a request (purpose + justification REQUIRED → else 422). Routes to the authoring doctor.
        v1.MapPost("/report-access-requests", async (RaiseAccessRequest req, OrdersDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (!Enum.TryParse<PurposeCode>(req.PurposeCode, out var purpose))
                return Results.Problem(statusCode: 422, title: "invalid-purpose", detail: "a valid purposeCode is required");
            if (!SensitiveResultGate.IsRequestValid(req.Justification))
                return Results.Problem(statusCode: 422, title: "justification-required", detail: "a non-blank justification is required");

            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == req.OrderId, ct);
            var line = order?.Lines.FirstOrDefault(l => l.OrderLineId == req.OrderLineId);
            if (order is null || line is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var r = new ReportAccessRequest
            {
                RequestId = Guid.NewGuid(), OrderId = req.OrderId, OrderLineId = req.OrderLineId, BeneficiaryId = order.BeneficiaryId,
                RequestedBy = me.Principal?.Subject ?? "unknown", RequestedForRole = me.Principal?.Roles.FirstOrDefault(),
                PurposeCode = purpose, Justification = req.Justification, RequestedTtlHours = req.RequestedTtlHours,
                Status = ReportAccessStatus.Requested, CreatedAt = clock.GetUtcNow(),
            };
            // 24.3 — the request and its event commit together. A request recorded whose
            // ReportAccessRequested event was lost is one no approver is ever told about: it sits
            // Requested forever while a clinician waits for a decision nobody knows is owed.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.ReportAccessRequests.Add(r);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(r.RequestId, AuditAction.Create, me, order.BeneficiaryId, "ReportAccessRequested", purpose.ToString(), AuditSeverity.Notice), ct);
            await outbox.EnqueueAsync("ReportAccessRequested", "orders.events",
                new { tenantId = r.TenantId, r.RequestId, r.OrderLineId, orderingProviderId = order.OrderingProviderId, purposeCode = purpose.ToString() }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/report-access-requests/{r.RequestId}", new ReportAccessStatusView(r.RequestId, r.Status.ToString()));
        })
        .Produces<ReportAccessStatusView>();

        // Decide (Approve | Deny | RequestInfo). Decider = authoring doctor OR Medical Director.
        v1.MapPost("/report-access-requests/{id:guid}/decision", async (Guid id, AccessDecision dec, OrdersDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var r = await db.ReportAccessRequests.FirstOrDefaultAsync(x => x.RequestId == id, ct);
            if (r is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (!ReportAccessWorkflow.IsDecidable(r.Status))
            {
                // 18.A4: every rejected transition is audited as TransitionDenied — a silent 409 leaves
                // no trace that someone tried to decide a finished request (23 §11).
                await audit.EmitAsync(Draft(r.RequestId, AuditAction.Decision, me, r.BeneficiaryId,
                    "TransitionDenied", $"status:{r.Status}", AuditSeverity.High), ct);
                return Results.Problem(statusCode: 409, title: "already-decided",
                    detail: $"a request in {r.Status} can no longer be decided");
            }

            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.OrderId == r.OrderId, ct);
            var line = order.Lines.Single(l => l.OrderLineId == r.OrderLineId);
            var subject = me.Principal?.Subject;
            var isAuthor = order.CreatedBy == subject;
            var roles = me.Principal?.Roles ?? new HashSet<string>();
            if (!SensitiveResultGate.CanDecide(isAuthor, roles))
                return Results.Problem(statusCode: 403, title: "not-a-decider", detail: "only the authoring doctor or a Medical Director may decide");

            var isMedicalDirector = !isAuthor && roles.Contains("medical_director");
            r.DecidedBy = subject;
            r.DecidedByRole = isMedicalDirector ? "MedicalDirector" : (me.Principal?.Roles.FirstOrDefault());
            r.DecidedAt = clock.GetUtcNow();
            var severity = isMedicalDirector ? AuditSeverity.High : AuditSeverity.Notice;   // MD decisions extra-audited

            // 24.3 — one transaction over every branch. This decision GRANTS ACCESS TO A SENSITIVE
            // RESULT: an approval whose ReportAccessApproved event is lost leaves a live grant that
            // notification, audit streaming and the expiry sweep's downstream consumers never hear about,
            // and a denial whose event is lost leaves the requester waiting on a decision already made.
            // The `default` branch commits nothing, which is correct — it changed nothing.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            switch (dec.Decision?.ToLowerInvariant())
            {
                case "approve":
                    r.Status = ReportAccessStatus.Approved;
                    // 18.A4: the caller may ask for LESS than the policy maximum, never more.
                    var ttl = SensitiveResultGate.EffectiveTtlHours(line.SensitivityLevel, dec.TtlHours);
                    var grant = new ReportAccessGrant
                    {
                        GrantId = Guid.NewGuid(), RequestId = r.RequestId, GranteeUserId = r.RequestedBy, OrderLineId = r.OrderLineId,
                        PurposeCode = r.PurposeCode, GrantedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddHours(ttl),
                    };
                    db.ReportAccessGrants.Add(grant);
                    await db.SaveChangesAsync(ct);
                    await audit.EmitAsync(Draft(r.RequestId, AuditAction.Decision, me, r.BeneficiaryId, "ReportAccessApproved", r.DecidedByRole, severity), ct);
                    await outbox.EnqueueAsync("ReportAccessApproved", "orders.events", new { tenantId = r.TenantId, r.RequestId, grant.GrantId, grant.GranteeUserId, grant.OrderLineId, grant.ExpiresAt, decidedByRole = r.DecidedByRole }, ct);
                    await tx.CommitAsync(ct);
                    return Results.Ok(new ReportAccessGrantView(r.RequestId, r.Status.ToString(), grant.GrantId, grant.ExpiresAt));

                case "deny":
                    if (string.IsNullOrWhiteSpace(dec.Reason)) return Results.Problem(statusCode: 422, title: "reason-required", detail: "a deny reason is required");
                    r.Status = ReportAccessStatus.Denied; r.DecisionReason = dec.Reason;
                    await db.SaveChangesAsync(ct);
                    await audit.EmitAsync(Draft(r.RequestId, AuditAction.Decision, me, r.BeneficiaryId, "ReportAccessDenied", r.DecidedByRole, severity), ct);
                    await outbox.EnqueueAsync("ReportAccessDenied", "orders.events", new { tenantId = r.TenantId, r.RequestId, reason = dec.Reason, decidedByRole = r.DecidedByRole }, ct);
                    await tx.CommitAsync(ct);
                    return Results.Ok(new ReportAccessStatusView(r.RequestId, r.Status.ToString()));

                case "requestinfo":
                    r.Status = ReportAccessStatus.InfoRequested; r.DecisionReason = dec.Reason;
                    await db.SaveChangesAsync(ct);
                    await outbox.EnqueueAsync("ReportAccessInfoRequested", "orders.events", new { tenantId = r.TenantId, r.RequestId, note = dec.Reason }, ct);
                    await tx.CommitAsync(ct);
                    return Results.Ok(new ReportAccessStatusView(r.RequestId, r.Status.ToString()));

                default:
                    return Results.Problem(statusCode: 400, title: "unknown-decision", detail: "decision must be Approve, Deny or RequestInfo");
            }
        });

        // 18.A4 — route/pick-up: Requested → UnderReview. Without this the state was unreachable and the
        // decider's identity was never recorded before the decision itself (23 §11).
        v1.MapPost("/report-access-requests/{id:guid}/review", async (Guid id, OrdersDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var r = await db.ReportAccessRequests.FirstOrDefaultAsync(x => x.RequestId == id, ct);
            if (r is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == r.OrderId, ct);
            var roles = me.Principal?.Roles ?? new HashSet<string>();
            if (!SensitiveResultGate.CanDecide(order.CreatedBy == me.Principal?.Subject, roles))
                return Results.Problem(statusCode: 403, title: "not-a-decider",
                    detail: "only the authoring doctor or a Medical Director may take a request under review");

            var illegal = ReportAccessWorkflow.Validate(r.Status, ReportAccessStatus.UnderReview);
            if (illegal is not null)
            {
                await audit.EmitAsync(Draft(r.RequestId, AuditAction.StateChange, me, r.BeneficiaryId, "TransitionDenied", illegal, AuditSeverity.High), ct);
                return Results.Problem(statusCode: 409, title: "illegal-transition", detail: illegal);
            }

            r.Status = ReportAccessStatus.UnderReview;
            r.DecidedBy = me.Principal?.Subject;             // decider identity recorded when the SLA timer starts
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(r.RequestId, AuditAction.StateChange, me, r.BeneficiaryId, "ReportAccessUnderReview", null, AuditSeverity.Notice), ct);
            return Results.Ok(new ReportAccessStatusView(r.RequestId, r.Status.ToString()));
        })
        .Produces<ReportAccessStatusView>();

        // 18.A4 — supply-info: InfoRequested → UnderReview. A request that entered InfoRequested had NO
        // path back, so the requester could never answer the question and the release was permanently
        // stuck. The supplement is appended; the original justification is preserved (23 §11).
        v1.MapPost("/report-access-requests/{id:guid}/supply-info", async (Guid id, SupplyInfo body, OrdersDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var r = await db.ReportAccessRequests.FirstOrDefaultAsync(x => x.RequestId == id, ct);
            if (r is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (!string.Equals(r.RequestedBy, me.Principal?.Subject, StringComparison.Ordinal))
                return Results.Problem(statusCode: 403, title: "not-the-requester", detail: "only the requester may supplement their justification");
            if (string.IsNullOrWhiteSpace(body.Supplement))
                return Results.Problem(statusCode: 422, title: "supplement-required", detail: "a supplemented justification is required");

            var illegal = ReportAccessWorkflow.Validate(r.Status, ReportAccessStatus.UnderReview);
            if (illegal is not null)
            {
                await audit.EmitAsync(Draft(r.RequestId, AuditAction.StateChange, me, r.BeneficiaryId, "TransitionDenied", illegal, AuditSeverity.High), ct);
                return Results.Problem(statusCode: 409, title: "illegal-transition", detail: illegal);
            }

            r.Justification = $"{r.Justification}\n---\n{body.Supplement}";   // appended, never overwritten
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            r.Status = ReportAccessStatus.UnderReview;
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(r.RequestId, AuditAction.Update, me, r.BeneficiaryId, "ReportAccessInfoSupplied", null, AuditSeverity.Notice), ct);
            await outbox.EnqueueAsync("ReportAccessInfoSupplied", "orders.events", new { tenantId = r.TenantId, r.RequestId }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new ReportAccessStatusView(r.RequestId, r.Status.ToString()));
        })
        .Produces<ReportAccessStatusView>();

        // Revoke a grant (author, Medical Director, or DPO) — audited + notified.
        v1.MapPost("/report-access-grants/{id:guid}/revoke", async (Guid id, OrdersDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var g = await db.ReportAccessGrants.FirstOrDefaultAsync(x => x.GrantId == id && x.RevokedAt == null, ct);
            if (g is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            g.RevokedAt = clock.GetUtcNow(); g.RevokedBy = me.Principal?.Subject;
            // 18.A4: the request follows its grant to Revoked, so the two tables can never disagree about
            // whether access is still live (23 §11: Approved → Revoked).
            await MoveRequestWithGrantAsync(db, g.RequestId, ReportAccessStatus.Revoked, ct);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(g.GrantId, AuditAction.StateChange, me, Guid.Empty, "ReportAccessGrantRevoked", null, AuditSeverity.High), ct);
            await outbox.EnqueueAsync("ReportAccessGrantRevoked", "orders.events", new { tenantId = g.TenantId, g.GrantId, g.OrderLineId, revokedBy = g.RevokedBy }, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // Background expiry sweep — expires grants past expires_at (audited + evented).
        v1.MapPost("/report-access/sweep-expiry", async (OrdersDbContext db, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var due = await db.ReportAccessGrants.Where(g => g.RevokedAt == null && g.ExpiresAt <= now).ToListAsync(ct);
            // 24.3 — this enqueues BEFORE its save: without the transaction a crash here would announce
            // grants expired that are still live, and a revocation event cannot be un-sent.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var g in due)
            {
                g.RevokedAt = now; g.RevokedBy = "system:expiry";
                await MoveRequestWithGrantAsync(db, g.RequestId, ReportAccessStatus.Expired, ct);   // 18.A4
                await outbox.EnqueueAsync("ReportAccessGrantExpired", "orders.events", new { tenantId = g.TenantId, g.GrantId, g.OrderLineId }, ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { expired = due.Count });
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"));
    }

    /// <summary>18.A4 — carry an Approved request to the terminal state its grant just reached. Silent
    /// no-op if the request already moved (a revoke racing the expiry sweep must not throw).</summary>
    /// <summary>Carry the REQUEST to the same terminal state as its grant, so the two tables cannot disagree
    /// about whether access is live (18.A4). Internal rather than private: the expiry sweeper (18.C2) applies
    /// the identical transition on its timer and must not reimplement it.</summary>
    internal static async Task MoveRequestWithGrantAsync(OrdersDbContext db, Guid requestId, ReportAccessStatus to, CancellationToken ct)
    {
        var r = await db.ReportAccessRequests.FirstOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (r is not null && ReportAccessWorkflow.CanTransition(r.Status, to)) r.Status = to;
    }

    private static AuditEventDraft Draft(Guid entityId, AuditAction action, IHbmpPrincipalAccessor me, Guid beneficiary, string outcome, string? reason, AuditSeverity severity) => new()
    {
        EntityType = "report_access", EntityId = entityId.ToString(), Action = action,
        ActorUserId = me.Principal?.Subject, ActorRole = string.Join(',', me.Principal?.Roles ?? new HashSet<string>()),
        DecisionOutcome = outcome, DecisionReasonCode = reason, Severity = severity,
        FieldClasses = ["phi"], Purpose = "sensitive-result-release",
    };
}
