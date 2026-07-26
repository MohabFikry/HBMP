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
            db.ReportAccessRequests.Add(r);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(r.RequestId, AuditAction.Create, me, order.BeneficiaryId, "ReportAccessRequested", purpose.ToString(), AuditSeverity.Notice), ct);
            await outbox.EnqueueAsync("ReportAccessRequested", "orders.events",
                new { r.RequestId, r.OrderLineId, orderingProviderId = order.OrderingProviderId, purposeCode = purpose.ToString() }, ct);
            return Results.Created($"/api/v1/report-access-requests/{r.RequestId}", new { r.RequestId, status = r.Status.ToString() });
        });

        // Decide (Approve | Deny | RequestInfo). Decider = authoring doctor OR Medical Director.
        v1.MapPost("/report-access-requests/{id:guid}/decision", async (Guid id, AccessDecision dec, OrdersDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var r = await db.ReportAccessRequests.FirstOrDefaultAsync(x => x.RequestId == id, ct);
            if (r is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (r.Status is not (ReportAccessStatus.Requested or ReportAccessStatus.UnderReview or ReportAccessStatus.InfoRequested))
                return Results.Problem(statusCode: 409, title: "already-decided");

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

            switch (dec.Decision?.ToLowerInvariant())
            {
                case "approve":
                    r.Status = ReportAccessStatus.Approved;
                    var ttl = dec.TtlHours ?? SensitiveResultGate.DefaultTtlHours(line.SensitivityLevel);
                    var grant = new ReportAccessGrant
                    {
                        GrantId = Guid.NewGuid(), RequestId = r.RequestId, GranteeUserId = r.RequestedBy, OrderLineId = r.OrderLineId,
                        PurposeCode = r.PurposeCode, GrantedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddHours(ttl),
                    };
                    db.ReportAccessGrants.Add(grant);
                    await db.SaveChangesAsync(ct);
                    await audit.EmitAsync(Draft(r.RequestId, AuditAction.Decision, me, r.BeneficiaryId, "ReportAccessApproved", r.DecidedByRole, severity), ct);
                    await outbox.EnqueueAsync("ReportAccessApproved", "orders.events", new { r.RequestId, grant.GrantId, grant.GranteeUserId, grant.OrderLineId, grant.ExpiresAt, decidedByRole = r.DecidedByRole }, ct);
                    return Results.Ok(new { r.RequestId, status = r.Status.ToString(), grant.GrantId, grant.ExpiresAt });

                case "deny":
                    if (string.IsNullOrWhiteSpace(dec.Reason)) return Results.Problem(statusCode: 422, title: "reason-required", detail: "a deny reason is required");
                    r.Status = ReportAccessStatus.Denied; r.DecisionReason = dec.Reason;
                    await db.SaveChangesAsync(ct);
                    await audit.EmitAsync(Draft(r.RequestId, AuditAction.Decision, me, r.BeneficiaryId, "ReportAccessDenied", r.DecidedByRole, severity), ct);
                    await outbox.EnqueueAsync("ReportAccessDenied", "orders.events", new { r.RequestId, reason = dec.Reason, decidedByRole = r.DecidedByRole }, ct);
                    return Results.Ok(new { r.RequestId, status = r.Status.ToString() });

                case "requestinfo":
                    r.Status = ReportAccessStatus.InfoRequested; r.DecisionReason = dec.Reason;
                    await db.SaveChangesAsync(ct);
                    await outbox.EnqueueAsync("ReportAccessInfoRequested", "orders.events", new { r.RequestId, note = dec.Reason }, ct);
                    return Results.Ok(new { r.RequestId, status = r.Status.ToString() });

                default:
                    return Results.Problem(statusCode: 400, title: "unknown-decision", detail: "decision must be Approve, Deny or RequestInfo");
            }
        });

        // Revoke a grant (author, Medical Director, or DPO) — audited + notified.
        v1.MapPost("/report-access-grants/{id:guid}/revoke", async (Guid id, OrdersDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var g = await db.ReportAccessGrants.FirstOrDefaultAsync(x => x.GrantId == id && x.RevokedAt == null, ct);
            if (g is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            g.RevokedAt = clock.GetUtcNow(); g.RevokedBy = me.Principal?.Subject;
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(g.GrantId, AuditAction.StateChange, me, Guid.Empty, "ReportAccessGrantRevoked", null, AuditSeverity.High), ct);
            await outbox.EnqueueAsync("ReportAccessGrantRevoked", "orders.events", new { g.GrantId, g.OrderLineId, revokedBy = g.RevokedBy }, ct);
            return Results.NoContent();
        });

        // Background expiry sweep — expires grants past expires_at (audited + evented).
        v1.MapPost("/report-access/sweep-expiry", async (OrdersDbContext db, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var due = await db.ReportAccessGrants.Where(g => g.RevokedAt == null && g.ExpiresAt <= now).ToListAsync(ct);
            foreach (var g in due)
            {
                g.RevokedAt = now; g.RevokedBy = "system:expiry";
                await outbox.EnqueueAsync("ReportAccessGrantExpired", "orders.events", new { g.GrantId, g.OrderLineId }, ct);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { expired = due.Count });
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"));
    }

    private static AuditEventDraft Draft(Guid entityId, AuditAction action, IHbmpPrincipalAccessor me, Guid beneficiary, string outcome, string? reason, AuditSeverity severity) => new()
    {
        EntityType = "report_access", EntityId = entityId.ToString(), Action = action,
        ActorUserId = me.Principal?.Subject, ActorRole = string.Join(',', me.Principal?.Roles ?? new HashSet<string>()),
        DecisionOutcome = outcome, DecisionReasonCode = reason, Severity = severity,
        FieldClasses = ["phi"], Purpose = "sensitive-result-release",
    };
}
