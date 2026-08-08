using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Finance.Domain;
using Mersal.Finance.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Finance.Api;

/// <summary>Phase 10.2 — utilization, provider settlements, financial summaries, audited exports, and the projection
/// seam. Every response is an <see cref="IFinanceProjection"/> (billing codes + amounts + masked-min PII only);
/// there is no clinical filter or column. Settlement submit/approve are SoD-split (approver ≠ submitter). Export is
/// a distinct high-severity <c>data.export</c> audit event with masked PII, row count, filter and correlation id.</summary>
public static class FinanceEndpoints
{
    public static void MapFinance(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/finance");

        // --- Utilization (authorized-vs-delivered, spend) --------------------------------------------------
        v1.MapGet("/utilization", async (FinanceDeps deps, CancellationToken ct,
            DateOnly? from, DateOnly? to, string? category, Guid? providerId, Guid? beneficiaryId) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.ReadUtilization, ct);
            if (denied is not null) return denied;
            var (f, t) = Window(from, to, deps.Calendar);
            var view = await deps.Queries.UtilizationAsync(deps.Tenant, f, t, category, providerId, beneficiaryId, ct);
            return Results.Ok(view);
        }).RequireAuthorization(HbmpPolicies.Scope("finance:read"));

        // --- Financial summaries (donor / leadership roll-ups) ---------------------------------------------
        v1.MapGet("/summaries", async (FinanceDeps deps, CancellationToken ct,
            DateOnly? from, DateOnly? to, string? dimension) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.ReadSummary, ct);
            if (denied is not null) return denied;
            var (f, t) = Window(from, to, deps.Calendar);
            var view = await deps.Queries.SummaryAsync(deps.Tenant, f, t, dimension ?? "serviceline", ct);
            return Results.Ok(view);
        }).RequireAuthorization(HbmpPolicies.Scope("finance:read"));

        // --- Settlements -----------------------------------------------------------------------------------
        v1.MapPost("/settlements", async (GenerateSettlementRequest req, HttpRequest http, FinanceDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.GenerateSettlement, ct);
            if (denied is not null) return denied;
            if (req.PeriodEnd < req.PeriodStart) return Unprocessable("bad-period", "period_end precedes period_start.");

            // Idempotency: generating a settlement mints a financial artifact, so a replayed Idempotency-Key returns
            // the settlement produced the first time rather than a duplicate. The header is honored when present
            // (the SPA sends it on every mutation); callers that omit it fall through to a normal generate.
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (!string.IsNullOrWhiteSpace(idem))
            {
                var prior = await deps.Db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
                if (prior is not null)
                {
                    var existing = await deps.Db.Settlements.AsNoTracking().Include(x => x.Lines)
                        .FirstOrDefaultAsync(x => x.SettlementId == prior.ResultId && x.TenantId == deps.Tenant, ct);
                    return existing is null
                        ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found")
                        : Results.Created($"/api/v1/finance/settlements/{existing.SettlementId}", SettlementView.From(existing));
                }
            }

            var bearer = http.Headers.Authorization.ToString();
            var s = await deps.Settlements.GenerateAsync(deps.Tenant, req.ProviderId, req.PeriodStart, req.PeriodEnd, deps.Subject, bearer, ct);
            deps.Db.Settlements.Add(s);
            if (!string.IsNullOrWhiteSpace(idem))
                deps.Db.ProcessedRequests.Add(new ProcessedRequest
                {
                    IdempotencyKey = idem, Operation = "settlement:generate",
                    ResultId = s.SettlementId, StatusCode = 201, CreatedAt = deps.Clock.GetUtcNow(),
                });
            try { await deps.Db.SaveChangesAsync(ct); }
            catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(idem))
            {
                // A concurrent request with the same key won the PK race — replay its settlement.
                var prior = await deps.Db.ProcessedRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idem, ct);
                var existing = prior is null ? null : await deps.Db.Settlements.AsNoTracking().Include(x => x.Lines)
                    .FirstOrDefaultAsync(x => x.SettlementId == prior.ResultId && x.TenantId == deps.Tenant, ct);
                return existing is null
                    ? Conflict("A settlement for this Idempotency-Key is being created concurrently.")
                    : Results.Created($"/api/v1/finance/settlements/{existing.SettlementId}", SettlementView.From(existing));
            }
            await Audit(deps, AuditAction.Create, s.SettlementId.ToString(), "SettlementGenerated", null, s.Status.ToString());
            return Results.Created($"/api/v1/finance/settlements/{s.SettlementId}", SettlementView.From(s));
        }).RequireAuthorization(HbmpPolicies.Scope("finance:write"));

        v1.MapGet("/settlements", async (FinanceDeps deps, CancellationToken ct, Guid? providerId, string? status) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.ReadSettlement, ct);
            if (denied is not null) return denied;
            var q = deps.Db.Settlements.AsNoTracking().Include(s => s.Lines).Where(s => s.TenantId == deps.Tenant);
            if (providerId is not null) q = q.Where(s => s.ProviderId == providerId);
            if (Enum.TryParse<SettlementStatus>(status, true, out var st)) q = q.Where(s => s.Status == st);
            var rows = await q.OrderByDescending(s => s.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(SettlementView.From).ToList());
        }).RequireAuthorization(HbmpPolicies.Scope("finance:read"));

        v1.MapGet("/settlements/{id:guid}", async (Guid id, FinanceDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.ReadSettlement, ct);
            if (denied is not null) return denied;
            var s = await deps.Db.Settlements.AsNoTracking().Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.SettlementId == id && x.TenantId == deps.Tenant, ct);
            return s is null ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found") : Results.Ok(SettlementView.From(s));
        }).RequireAuthorization(HbmpPolicies.Scope("finance:read"));

        v1.MapPost("/settlements/{id:guid}/submit", async (Guid id, FinanceDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.SubmitSettlement, ct);
            if (denied is not null) return denied;
            var s = await deps.Db.Settlements.FirstOrDefaultAsync(x => x.SettlementId == id && x.TenantId == deps.Tenant, ct);
            if (s is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (s.Status != SettlementStatus.Draft) return Conflict($"Only a Draft settlement can be submitted (is {s.Status}).");

            s.Status = SettlementStatus.Submitted;
            s.SubmittedBy = deps.Subject;
            s.SubmittedAt = deps.Clock.GetUtcNow();
            s.UpdatedAt = deps.Clock.GetUtcNow();
            try { await deps.Db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict("This settlement changed concurrently."); }
            await Audit(deps, AuditAction.StateChange, id.ToString(), "SettlementSubmitted", "Draft", "Submitted");
            return Results.Ok(SettlementView.From(s));
        }).RequireAuthorization(HbmpPolicies.Scope("finance:write"));

        // SoD (11-permission-matrix release rule): the approver MUST be a different principal than the submitter.
        v1.MapPost("/settlements/{id:guid}/approve", async (Guid id, FinanceDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.ApproveSettlement, ct);
            if (denied is not null) return denied;
            var s = await deps.Db.Settlements.FirstOrDefaultAsync(x => x.SettlementId == id && x.TenantId == deps.Tenant, ct);
            if (s is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (s.Status != SettlementStatus.Submitted) return Conflict($"Only a Submitted settlement can be approved (is {s.Status}).");
            if (string.Equals(s.SubmittedBy, deps.Subject, StringComparison.Ordinal))
                return Results.Problem(statusCode: 409, title: "segregation-of-duties",
                    detail: "The approver must be a different person than the submitter.", type: "urn:hbmp:sod-violation");

            // 24.3 — money. An approved settlement whose SettlementApproved event was lost is a payment
            // authorised here and never announced to anything downstream that acts on it.
            await using var tx = await deps.Db.Database.BeginTransactionAsync(ct);
            s.Status = SettlementStatus.Approved;
            s.ApprovedBy = deps.Subject;
            s.ApprovedAt = deps.Clock.GetUtcNow();
            s.UpdatedAt = deps.Clock.GetUtcNow();
            try { await deps.Db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict("This settlement changed concurrently."); }
            await deps.Outbox.EnqueueAsync("SettlementApproved", "finance.events",
                new { settlementId = id, s.SettlementNo, providerId = s.ProviderId, total = s.Total }, ct);
            await Audit(deps, AuditAction.Decision, id.ToString(), "SettlementApproved", "Submitted", "Approved", AuditSeverity.Notice);
            await tx.CommitAsync(ct);
            return Results.Ok(SettlementView.From(s));
        }).RequireAuthorization(HbmpPolicies.Scope("finance:approve"));

        // --- Export (distinct elevated action; masked PII; high-severity data.export audit) -----------------
        v1.MapPost("/exports", async (ExportRequest req, HttpRequest http, FinanceDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.Export, ct);
            if (denied is not null) return denied;

            var view = await deps.Queries.UtilizationAsync(deps.Tenant, req.From, req.To, req.Category, req.ProviderId, null, ct);
            var (csv, rows) = FinanceQueries.ToCsv(view);
            var filter = $"report={req.Report};from={req.From};to={req.To};category={req.Category};provider={req.ProviderId}";
            var correlation = http.HttpContext?.TraceIdentifier;

            deps.Db.Exports.Add(new ExportRecord
            {
                TenantId = deps.Tenant, Report = req.Report, Format = req.Format ?? "csv", Filter = filter,
                RowCount = rows, RequestedBy = deps.Subject, CorrelationId = correlation, CreatedAt = deps.Clock.GetUtcNow(),
            });
            await deps.Db.SaveChangesAsync(ct);

            // data.export is a distinct HIGH-severity audit event (19-audit §): actor, filter, row count, correlation.
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "finance_export", EntityId = req.Report, Action = AuditAction.Export,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                AfterState = $"rows={rows}", DecisionReasonCode = filter, Severity = AuditSeverity.High,
            }, ct);

            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"{req.Report}-{req.From}_{req.To}.csv");
        }).RequireAuthorization(HbmpPolicies.Scope("finance:export"));

        // --- Projection seam (system) ----------------------------------------------------------------------
        v1.MapPost("/projections", async (ProjectRequest req, FinanceDeps deps, FinanceEventProjector projector, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.Project, ct);
            if (denied is not null) return denied;
            var handled = await projector.ProjectAsync(
                new FinanceEvent(req.EventId, req.EventType, req.TenantId, req.Fields, req.OccurredAt), ct);
            return Results.Ok(new { handled });
        }).RequireAuthorization(HbmpPolicies.Scope("finance:project"));
    }

    private static (DateOnly From, DateOnly To) Window(DateOnly? from, DateOnly? to, IBusinessCalendar calendar)
    {
        var t = to ?? calendar.Today();   // 18.A3 — Cairo business date
        var f = from ?? t.AddMonths(-1);
        return (f, t);
    }

    private static async Task Audit(FinanceDeps deps, AuditAction action, string entityId, string outcome,
        string? before, string? after, AuditSeverity severity = AuditSeverity.Info) =>
        await deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "settlement", EntityId = entityId, Action = action,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
            BeforeState = before, AfterState = after, DecisionOutcome = outcome, Severity = severity,
        });

    private static IResult Unprocessable(string title, string detail) =>
        Results.Problem(statusCode: 422, title: title, detail: detail, type: "urn:hbmp:validation");

    private static IResult Conflict(string detail) =>
        Results.Problem(statusCode: 409, title: "conflict", detail: detail, type: "urn:hbmp:conflict");
}
