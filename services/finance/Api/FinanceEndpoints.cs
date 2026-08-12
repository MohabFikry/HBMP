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
        }).RequireAuthorization(HbmpPolicies.Scope("finance:read"))
        .Produces<UtilizationView>();

        // --- Financial summaries (donor / leadership roll-ups) ---------------------------------------------
        v1.MapGet("/summaries", async (FinanceDeps deps, CancellationToken ct,
            DateOnly? from, DateOnly? to, string? dimension) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.ReadSummary, ct);
            if (denied is not null) return denied;
            var (f, t) = Window(from, to, deps.Calendar);
            var view = await deps.Queries.SummaryAsync(deps.Tenant, f, t, dimension ?? "serviceline", ct);
            return Results.Ok(view);
        }).RequireAuthorization(HbmpPolicies.Scope("finance:read"))
        .Produces<FinancialSummaryView>();

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
        }).RequireAuthorization(HbmpPolicies.Scope("finance:write"))
        .Produces<SettlementView>();

        v1.MapGet("/settlements", async (FinanceDeps deps, HttpResponse http, CancellationToken ct,
            Guid? providerId, string? status) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.ReadSettlement, ct);
            if (denied is not null) return denied;
            var q = deps.Db.Settlements.AsNoTracking().Include(s => s.Lines).Where(s => s.TenantId == deps.Tenant);
            if (providerId is not null) q = q.Where(s => s.ProviderId == providerId);
            if (Enum.TryParse<SettlementStatus>(status, true, out var st)) q = q.Where(s => s.Status == st);
            var rows = await q.OrderByDescending(s => s.CreatedAt).Take(Cap).ToListAsync(ct);

            // How many there ACTUALLY are, so the screen can say it is showing 100 of 340 rather than
            // presenting a truncated list as a complete one (invariant 31). The COUNT is skipped when the
            // page came back short, because then the page IS the total and a second query would ask the
            // database a question already answered.
            var total = rows.Count < Cap ? rows.Count : await q.CountAsync(ct);
            http.Headers["X-Total-Count"] = total.ToString();
            return Results.Ok(rows.Select(SettlementView.From).ToList());
        }).RequireAuthorization(HbmpPolicies.Scope("finance:read"))
        .Produces<IEnumerable<SettlementView>>();

        v1.MapGet("/settlements/{id:guid}", async (Guid id, FinanceDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.ReadSettlement, ct);
            if (denied is not null) return denied;
            var s = await deps.Db.Settlements.AsNoTracking().Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.SettlementId == id && x.TenantId == deps.Tenant, ct);
            return s is null ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found") : Results.Ok(SettlementView.From(s));
        }).RequireAuthorization(HbmpPolicies.Scope("finance:read"))
        .Produces<SettlementView>();

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
        }).RequireAuthorization(HbmpPolicies.Scope("finance:write"))
        .Produces<SettlementView>();

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
        }).RequireAuthorization(HbmpPolicies.Scope("finance:approve"))
        .Produces<SettlementView>();

        // --- Export (distinct elevated action; masked PII; high-severity data.export audit) -----------------
        v1.MapPost("/exports", async (ExportRequest req, HttpRequest http, FinanceDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.Export, ct);
            if (denied is not null) return denied;

            // A FORMAT THIS ENDPOINT DOES NOT PRODUCE IS REFUSED, not recorded.
            //
            // The portal offered CSV and XLSX; this handler has always returned `text/csv`. The claimed
            // format was nonetheless written into `ExportRecord.Format`, so the export ledger asserted
            // spreadsheets that were never generated. XLSX is gone from the portal (a CSV opens in Excel;
            // the gap is not worth a spreadsheet library in the one service whose security argument is that
            // it cannot express a clinical field) — and refused here, because the portal is not the only
            // caller and a silent substitution is how the ledger became wrong in the first place.
            var format = string.IsNullOrWhiteSpace(req.Format) ? "csv" : req.Format.ToLowerInvariant();
            if (format != "csv")
                return Unprocessable("unsupported-format", $"This endpoint produces CSV; '{req.Format}' is not available.");
            if (req.To < req.From) return Unprocessable("bad-period", "to precedes from.");

            // THE REPORT SELECTOR NOW SELECTS.
            //
            // Every report ran `UtilizationAsync`; `Report` named the file and the audit event and was
            // otherwise unread. Asking for settlements produced utilization rows in a file called
            // `settlement-….csv` AND wrote a high-severity `data.export` event asserting a settlement export
            // that did not happen — a record naming an action nobody performed, which is worse than no
            // record, because the record is the thing an auditor trusts.
            //
            // An unknown report is refused rather than falling back to utilization: a fallback is how the
            // original defect would read from the outside.
            //
            // Normalised once. The record declares `Report` non-nullable, but it arrives from JSON, where a
            // missing property is null however the type is written — so it is coalesced here rather than
            // trusted, and the local is what the ledger and the audit event then record.
            var report = req.Report ?? "";
            (string? csv, int rows) = report.ToLowerInvariant() switch
            {
                "settlement" or "settlements" => FinanceQueries.ToCsv(
                    await deps.Queries.SettlementsForExportAsync(deps.Tenant, req.From, req.To, req.ProviderId, ct)),
                "summary" => FinanceQueries.ToCsv(
                    await deps.Queries.SummaryAsync(deps.Tenant, req.From, req.To, req.Dimension ?? "serviceline", ct)),
                "utilization" => FinanceQueries.ToCsv(
                    await deps.Queries.UtilizationAsync(deps.Tenant, req.From, req.To, req.Category, req.ProviderId, null, ct)),
                _ => (null, 0),
            };
            if (csv is null)
                return Unprocessable("unknown-report",
                    $"'{report}' is not a report. Known reports: utilization, settlement, summary.");

            var filter = $"report={report};from={req.From};to={req.To};category={req.Category};provider={req.ProviderId}";
            var correlation = http.HttpContext?.TraceIdentifier;

            deps.Db.Exports.Add(new ExportRecord
            {
                // `format` — the one that was produced — never `req.Format`, the one that was asked for.
                TenantId = deps.Tenant, Report = report, Format = format, Filter = filter,
                RowCount = rows, RequestedBy = deps.Subject, CorrelationId = correlation, CreatedAt = deps.Clock.GetUtcNow(),
            });
            await deps.Db.SaveChangesAsync(ct);

            // data.export is a distinct HIGH-severity audit event (19-audit §): actor, filter, row count, correlation.
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "finance_export", EntityId = report, Action = AuditAction.Export,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                AfterState = $"rows={rows}", DecisionReasonCode = filter, Severity = AuditSeverity.High,
            }, ct);

            // The audited row count, on a HEADER.
            //
            // The response body is a file, so there is nowhere in it to put the figure — and the SPA's
            // Exports screen reported a count while downloading nothing at all, because its client read this
            // response as JSON. Now that it receives the file, this is how it still gets the number it
            // shows the operator, and the number it shows is the number the audit event recorded.
            //
            // Kong must list this in `exposed_headers` or cross-origin JavaScript cannot read it — the trap
            // `X-Active-Branch` documents in that file and `X-Total-Count` hit last pass.
            http.HttpContext!.Response.Headers["X-Row-Count"] = rows.ToString();
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"{req.Report}-{req.From}_{req.To}.csv");
        }).RequireAuthorization(HbmpPolicies.Scope("finance:export"))
        // A CSV, not JSON. Declaring a schema here would publish a shape this endpoint
        // never returns; what a caller needs to know is the CONTENT TYPE.
        .Produces<byte[]>(StatusCodes.Status200OK, contentType: "text/csv");

        // --- Projection seam (system) ----------------------------------------------------------------------
        //
        // The same pairing as reporting's: the policy rule names no roles, which is right for a client
        // credential and wrong for a person, so `finance:project` is `service_only` in the identity
        // catalogue (identity 0039) and the `finance` role's seeded grant is revoked. The tenant comes from
        // the principal, not the body, so a caller cannot write cost facts into another tenant's ledger.
        v1.MapPost("/projections", async (ProjectRequest req, FinanceDeps deps, FinanceEventProjector projector, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(FinancePolicies.Project, ct);
            if (denied is not null) return denied;

            if (!string.IsNullOrWhiteSpace(req.TenantId) && !string.Equals(req.TenantId, deps.Tenant, StringComparison.Ordinal))
                return Unprocessable("tenant-mismatch",
                    "A projection may only be written for the tenant the caller is authenticated for.");

            var handled = await projector.ProjectAsync(
                new FinanceEvent(req.EventId, req.EventType, deps.Tenant, req.Fields, req.OccurredAt), ct);

            // Audited for the same reason reporting's is: a cost fact nobody can trace is a number on a
            // finance report with no provenance.
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "finance_projection", EntityId = req.EventId.ToString(), Action = AuditAction.Create,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                DecisionReasonCode = req.EventType,
                AfterState = handled ? "projected" : "deduplicated",
                Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Ok(new { handled });
        }).RequireAuthorization(HbmpPolicies.Scope("finance:project"));
    }

    /// <summary>The settlement list's page size. Matches <see cref="FinanceQueries.ExportCap"/> so the file
    /// and the screen answer the same question.</summary>
    private const int Cap = FinanceQueries.ExportCap;

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
