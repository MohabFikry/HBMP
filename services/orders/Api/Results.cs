using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>Phase 5.3 — result upload + routing (US-042). A performing provider uploads result value(s) and an
/// optional report for a line it has CONSUMED: the report goes to document-service (scanned, CMK blob) and its ref
/// is pinned on the fulfillment row; a routing event (OrderResultUploaded) is emitted so notification-service tells
/// the ordering doctor (and the approval team if the order was gated). Min-necessary: the result is readable only by
/// the ordering doctor (treating) and the approval team, never unrelated roles/facilities. PHI reads audited.</summary>
public static class ResultEndpoints
{
    public static void MapResults(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/investigation-orders").RequireAuthorization();

        // ---- Upload a result for a consumed line ----
        v1.MapPost("/{orderId:guid}/lines/{lineId:guid}/result", async (
            Guid orderId, Guid lineId, HttpRequest http, OrdersDbContext db, FulfillmentGate gate,
            IReportDocumentClient docs, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null) return Results.NotFound();
            var line = order.Lines.FirstOrDefault(l => l.OrderLineId == lineId);
            if (line is null) return Results.NotFound();

            var denied = await gate.AuthorizeConsumeAsync(order.OrderType, ct);
            if (denied is not null) return denied;

            var provider = Guid.TryParse(me.Principal?.ProviderId, out var pg) ? pg : Guid.Empty;

            // A result may only be attached to a line THIS provider has consumed (append-only fulfillment must exist).
            var fulfillments = await db.Fulfillments.Where(f => f.OrderLineId == lineId).ToListAsync(ct);
            if (fulfillments.Count == 0)
                return Results.Problem(statusCode: 409, title: "line-not-consumed", type: "urn:hbmp:line-not-consumed",
                    detail: "A result can only be uploaded for a line that has been consumed.");
            if (fulfillments.All(f => f.PerformingProviderId != provider))
                return Results.Problem(statusCode: 403, title: "not-performing-provider", type: "urn:hbmp:not-performing-provider",
                    detail: "Only the provider that performed this line may upload its result.");

            var form = http.HasFormContentType ? await http.ReadFormAsync(ct) : null;
            var resultValue = form? ["resultValue"].ToString();
            var report = form?.Files.GetFile("report");

            Guid? documentId = null;
            if (report is not null && report.Length > 0)
            {
                using var ms = new MemoryStream();
                await report.CopyToAsync(ms, ct);
                documentId = await docs.StoreReportAsync(order.BeneficiaryId, report.FileName, report.ContentType, ms.ToArray(),
                    http.Headers.Authorization.ToString(), ct);
                if (documentId is null)
                    return Results.Problem(statusCode: 502, title: "report-store-failed", type: "urn:hbmp:report-store-failed",
                        detail: "The report could not be stored (rejected, quarantined, or document-service unreachable).");
            }
            if (string.IsNullOrWhiteSpace(resultValue) && documentId is null)
                return Results.Problem(statusCode: 400, title: "empty-result", type: "urn:hbmp:empty-result",
                    detail: "Provide a resultValue and/or a report file.");

            // Attach to this provider's fulfillment for the line (the most recent still awaiting a result).
            var target = fulfillments.Where(f => f.PerformingProviderId == provider)
                .OrderByDescending(f => f.ResultUploadedAt is null).ThenByDescending(f => f.ConsumedAt).First();
            target.ResultValue = string.IsNullOrWhiteSpace(resultValue) ? target.ResultValue : resultValue;
            target.ResultDocumentId = documentId ?? target.ResultDocumentId;
            target.ResultUploadedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            // Route to the ordering doctor (+ approvals if the order was approval-gated).
            await outbox.EnqueueAsync("OrderResultUploaded", "orders.events", new
            {
                orderId, lineId, fulfillmentId = target.FulfillmentId, order.OrderNo,
                orderingProviderId = order.OrderingProviderId, beneficiaryId = order.BeneficiaryId,
                approvalGated = order.AuthorizationId is not null, resultDocumentId = target.ResultDocumentId,
                sensitivityLevel = line.SensitivityLevel.ToString(),
            }, ct);

            // 14.6 — a result against a non-Standard line is content-restricted; announce it so downstream
            // (14.7 gate) and notifications treat it as special-category from the moment it lands.
            if (line.SensitivityLevel != SensitivityLevel.Standard)
                await outbox.EnqueueAsync("SensitiveResultRestricted", "orders.events", new
                {
                    orderId, lineId, order.OrderNo, beneficiaryId = order.BeneficiaryId,
                    orderingProviderId = order.OrderingProviderId, sensitivityLevel = line.SensitivityLevel.ToString(),
                }, ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "order_fulfillment", EntityId = target.FulfillmentId.ToString(), Action = AuditAction.Update,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "ResultUploaded",
                DecisionReasonCode = $"order:{orderId};line:{lineId}", FieldClasses = ["phi"],
            }, ct);

            return Results.Ok(ResultResponse.From(target));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:consume")).DisableAntiforgery();

        // ---- Read a line's result (ordering doctor [treating] or approval team) ----
        v1.MapGet("/{orderId:guid}/lines/{lineId:guid}/result", async (
            Guid orderId, Guid lineId, HttpRequest http, OrdersDbContext db, OrdersGate gate,
            IAuthorizationEngine engine, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            var line = order?.Lines.FirstOrDefault(l => l.OrderLineId == lineId);
            if (order is null || line is null) return Results.NotFound();

            // 14.7 — a NON-Standard result is default-deny except the authoring doctor or an active grant holder.
            // This deliberately OVERRIDES the approval team's standing EMR oversight (design 37 §6).
            if (line.SensitivityLevel != SensitivityLevel.Standard)
            {
                var subject = me.Principal?.Subject;
                var isAuthor = order.CreatedBy == subject;
                var now = DateTimeOffset.UtcNow;
                var activeGrant = subject is null ? null : await db.ReportAccessGrants.AsNoTracking()
                    .Where(g => g.GranteeUserId == subject && g.OrderLineId == lineId && g.RevokedAt == null && now < g.ExpiresAt)
                    .OrderByDescending(g => g.GrantedAt).FirstOrDefaultAsync(ct);

                if (SensitiveResultGate.Decide(line.SensitivityLevel, isAuthor, activeGrant is not null) == ResultDisclosure.ExistenceOnly)
                {
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "order_fulfillment", EntityId = lineId.ToString(), Action = AuditAction.Read,
                        ActorUserId = subject, DecisionOutcome = "ExistenceOnly", DecisionReasonCode = "sensitive-restricted", Severity = AuditSeverity.Notice,
                    }, ct);
                    // Existence metadata ONLY — never values, never a document ref.
                    return Results.Ok(new
                    {
                        restricted = true, orderId, lineId, sensitivityLevel = line.SensitivityLevel.ToString(),
                        category = line.CodeSystem.ToString(), status = line.Status.ToString(), orderingBranchId = order.OrderingBranchId,
                    });
                }

                var sensitive = await db.Fulfillments.AsNoTracking()
                    .Where(f => f.OrderLineId == lineId && f.ResultUploadedAt != null).OrderBy(f => f.ConsumedAt).ToListAsync(ct);
                await audit.EmitAsync(activeGrant is not null && !isAuthor
                    ? new AuditEventDraft   // a DISTINCT read-under-grant event (grant id + purpose + actor + result ref)
                    {
                        EntityType = "report_access_grant", EntityId = activeGrant.GrantId.ToString(), Action = AuditAction.Read,
                        ActorUserId = subject, DecisionOutcome = "SensitiveResultReadUnderGrant", DecisionReasonCode = activeGrant.PurposeCode.ToString(),
                        Severity = AuditSeverity.High, FieldClasses = ["phi"],
                    }
                    : new AuditEventDraft   // the authoring doctor's ordinary (audited) PHI read
                    {
                        EntityType = "order_fulfillment", EntityId = lineId.ToString(), Action = AuditAction.Read,
                        ActorUserId = subject, DecisionOutcome = "Allow", DecisionReasonCode = "author", FieldClasses = ["phi"],
                    }, ct);
                return Results.Ok(sensitive.Select(ResultResponse.From));
            }

            var denied = await AuthorizeResultReadAsync(gate, engine, me, order.BeneficiaryId, orderId.ToString(),
                http.Headers.Authorization.ToString(), ct);
            if (denied is not null) return denied;

            var results = await db.Fulfillments.AsNoTracking()
                .Where(f => f.OrderLineId == lineId && (f.ResultUploadedAt != null))
                .OrderBy(f => f.ConsumedAt).ToListAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "order_fulfillment", EntityId = lineId.ToString(), Action = AuditAction.Read,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Allow", FieldClasses = ["phi"],
            }, ct);
            return Results.Ok(results.Select(ResultResponse.From));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));
    }

    /// <summary>Result read is min-necessary (11-permission-matrix): the ordering doctor (treating) OR the approval
    /// team may read; anyone else is denied and audited. Doctor path reuses the treating gate; the oversight path is
    /// a tenant-scoped engine check on <c>order_result</c>.</summary>
    private static async Task<IResult?> AuthorizeResultReadAsync(
        OrdersGate gate, IAuthorizationEngine engine, IHbmpPrincipalAccessor me, Guid beneficiaryId, string orderId,
        string? bearer, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null) return Results.Problem(statusCode: 401, title: "unauthenticated", type: "urn:hbmp:unauthenticated");

        if (p.Roles.Contains("approvals_team") || p.Roles.Contains("medical_director"))
        {
            var decision = await engine.EvaluateAsync(new AuthzRequest(
                p, OrdersPolicies.ReadResult, new ResourceRef { Type = "order_result", TenantId = p.TenantId }, "oversight"), ct);
            return decision.IsAllowed ? null : Results.Problem(statusCode: 403, title: "access-denied",
                type: "urn:hbmp:orders-access-denied", detail: "You may not read this result.");
        }
        // Ordering doctor: treating-relationship on the order's beneficiary (engine audits the decision).
        return await gate.CheckAsync(OrdersPolicies.ReadResult, orderId, beneficiaryId, bearer, ct);
    }
}
