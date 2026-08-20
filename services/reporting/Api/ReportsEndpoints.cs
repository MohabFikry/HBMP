using System.Globalization;
using System.Text;
using System.Text.Json;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Reporting.Domain;
using Mersal.Reporting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Reporting.Api;

/// <summary>Phase 8.2 report APIs (US-073): aggregate, PHI-free KPI reads split by data zone (operational /
/// clinical-coded / financial), a system projection seam, an audited export, and an async job handle for long
/// ranges (NFR-006). Every read is tenant-scoped from the caller's principal.</summary>
public static class ReportsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapReports(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/reports");

        // ── Operational zone ───────────────────────────────────────────────────────────────────────────────
        v1.MapGet("/approval-tat", (string? from, string? to, ReportContext cx, CancellationToken ct) =>
            cx.RunOrQueue(ReportingPolicies.ReadOperational, "approval-tat", from, to,
                (f, t) => cx.Q.ApprovalTatAsync(cx.Tenant, f, t, ct), ct))
            .RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<ApprovalTatReport>()
            .Produces<JobHandleView>(StatusCodes.Status202Accepted);

        v1.MapGet("/pending-approvals", async (ReportContext cx, CancellationToken ct) =>
        {
            var denied = await cx.Gate.CheckAsync(ReportingPolicies.ReadOperational, ct);
            return denied ?? Results.Ok(await cx.Q.PendingApprovalsAsync(cx.Tenant, ct));
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<PendingApprovalsReport>();

        v1.MapGet("/clinic-workload", (string? from, string? to, ReportContext cx, CancellationToken ct) =>
            cx.RunOrQueue(ReportingPolicies.ReadOperational, "clinic-workload", from, to,
                (f, t) => cx.Q.ClinicWorkloadAsync(cx.Tenant, f, t, ct), ct))
            .RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<ClinicWorkloadReport>()
            .Produces<JobHandleView>(StatusCodes.Status202Accepted);

        v1.MapGet("/utilization", (string? dimension, string? from, string? to, ReportContext cx, CancellationToken ct) =>
        {
            if (!Enum.TryParse<UtilizationDimension>(dimension, ignoreCase: true, out var dim))
                return Task.FromResult(Results.Problem(statusCode: 400, title: "invalid-dimension",
                    detail: "dimension must be one of provider|drug|lab|radiology.", type: "urn:hbmp:validation"));
            return cx.RunOrQueue(ReportingPolicies.ReadOperational, "utilization", from, to,
                (f, t) => cx.Q.UtilizationAsync(cx.Tenant, dim, f, t, ct: ct), ct);
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<UtilizationReport>()
            .Produces<JobHandleView>(StatusCodes.Status202Accepted);

        v1.MapGet("/no-show", (string? from, string? to, ReportContext cx, CancellationToken ct) =>
            cx.RunOrQueue(ReportingPolicies.ReadOperational, "no-show", from, to,
                (f, t) => cx.Q.NoShowAsync(cx.Tenant, f, t, ct), ct))
            .RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<NoShowReport>()
            .Produces<JobHandleView>(StatusCodes.Status202Accepted);

        v1.MapGet("/rejected-requests", (string? from, string? to, ReportContext cx, CancellationToken ct) =>
            cx.RunOrQueue(ReportingPolicies.ReadOperational, "rejected-requests", from, to,
                (f, t) => cx.Q.RejectedRequestsAsync(cx.Tenant, f, t, ct), ct))
            .RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<RejectedRequestsReport>()
            .Produces<JobHandleView>(StatusCodes.Status202Accepted);

        // ── Clinical-coded zone (NOT finance) ─────────────────────────────────────────────────────────────
        v1.MapGet("/top-diagnoses", (string? from, string? to, ReportContext cx, CancellationToken ct) =>
            cx.RunOrQueue(ReportingPolicies.ReadClinical, "top-diagnoses", from, to,
                (f, t) => cx.Q.TopCodesAsync(cx.Tenant, CodeKind.Diagnosis, f, t, ct: ct), ct))
            .RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<TopCodesReport>()
            .Produces<JobHandleView>(StatusCodes.Status202Accepted);

        v1.MapGet("/top-medications", (string? from, string? to, ReportContext cx, CancellationToken ct) =>
            cx.RunOrQueue(ReportingPolicies.ReadClinical, "top-medications", from, to,
                (f, t) => cx.Q.TopCodesAsync(cx.Tenant, CodeKind.Medication, f, t, ct: ct), ct))
            .RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<TopCodesReport>()
            .Produces<JobHandleView>(StatusCodes.Status202Accepted);

        // ── Financial zone ──────────────────────────────────────────────────────────────────────────────
        v1.MapGet("/financial-summary", (string? from, string? to, ReportContext cx, CancellationToken ct) =>
            cx.RunOrQueue(ReportingPolicies.ReadFinancial, "financial-summary", from, to,
                (f, t) => cx.Q.FinancialSummaryAsync(cx.Tenant, f, t, ct), ct))
            .RequireAuthorization(HbmpPolicies.Scope("reporting:read-financial"));

        // ── Async job poll ─────────────────────────────────────────────────────────────────────────────
        v1.MapGet("/jobs/{id:guid}", async (Guid id, ReportContext cx, CancellationToken ct) =>
        {
            var denied = await cx.Gate.CheckAsync(ReportingPolicies.ReadOperational, ct);
            if (denied is not null) return denied;
            var job = await cx.Db.ReportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == id && j.TenantId == cx.Tenant, ct);
            if (job is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            JsonElement? result = job.ResultJson is null ? null : JsonSerializer.Deserialize<JsonElement>(job.ResultJson);
            return Results.Ok(new { job.JobId, job.Report, job.Status, job.ProgressPercent, result });
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<FinancialSummaryReport>()
            .Produces<JobHandleView>(StatusCodes.Status202Accepted);

        // ── Audited export (CSV) ─────────────────────────────────────────────────────────────────────────
        v1.MapGet("/{report}/export", async (string report, string? from, string? to, ReportContext cx, CancellationToken ct) =>
        {
            var denied = await cx.Gate.CheckAsync(ReportingPolicies.Export, ct);
            if (denied is not null) return denied;
            var (f, t) = Api.Period.Parse(from, to, cx.Calendar);

            (string csv, int rows) = report switch
            {
                "financial-summary" => FinancialCsv(await cx.Q.FinancialSummaryAsync(cx.Tenant, f, t, ct)),
                "approval-tat" => TatCsv(await cx.Q.ApprovalTatAsync(cx.Tenant, f, t, ct)),
                _ => ("", -1),
            };
            if (rows < 0) return Results.Problem(statusCode: 400, title: "unknown-report", type: "urn:hbmp:validation");

            // Every export writes an audit event (actor, report, filter, row count) — 19-audit-strategy.
            await cx.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "report", EntityId = report, Action = AuditAction.Export,
                ActorUserId = cx.Me.Principal?.Subject, TenantId = cx.Tenant,
                DecisionReasonCode = $"{f:O}..{t:O}", AfterState = $"rows={rows}", Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Text(csv, "text/csv");
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:export"))
        // 21.4 — `reporting_extracts` gates EXTRACTS ONLY, not reporting as a whole: a tenant not on the
        // extracts programme still reads its own dashboards and views on screen. That is why reporting
        // uses the per-endpoint filter where the other ten module services gate the whole service.
        .RequireFeature(ProgramFeatures.ReportingExtracts);

        // ── Executive dashboard (phase 8.3) — composed widget contracts, each with an accessible dataTable +
        // bilingual labels. Gated on the operational zone; clinical + financial widgets are included only for a
        // caller authorized for those zones (finance widgets exclude diagnoses by construction).
        var dash = app.MapGroup("/api/v1/dashboards");
        dash.MapGet("/executive", async (string? from, string? to, ReportContext cx, DashboardBuilder builder, CancellationToken ct) =>
        {
            var denied = await cx.Gate.CheckAsync(ReportingPolicies.ReadOperational, ct);
            if (denied is not null) return denied;
            var clinical = await cx.Gate.CheckAsync(ReportingPolicies.ReadClinical, ct) is null;
            var financial = await cx.Gate.CheckAsync(ReportingPolicies.ReadFinancial, ct) is null;
            var (f, t) = Api.Period.Parse(from, to, cx.Calendar);
            var payload = await builder.BuildAsync(cx.Tenant, f, t, clinical, financial, ct);
            return Results.Ok(payload);
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:read"))
            .Produces<ExecutiveDashboard>();

        // ── System projection seam ───────────────────────────────────────────────────────────────────────
        v1.MapPost("/projections", async (ProjectionRequest req, ReportContext cx, CancellationToken ct) =>
        {
            var denied = await cx.Gate.CheckAsync(ReportingPolicies.Project, ct);
            if (denied is not null) return denied;
            var ev = new ReportingEvent(req.EventId, req.EventType, req.TenantId, req.Fields ?? [],
                req.OccurredAt ?? cx.Clock.GetUtcNow());
            var projected = await cx.Projector.ProjectAsync(ev, ct);
            return Results.Ok(new { projected });
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:project"));
    }

    private static (string, int) FinancialCsv(FinancialSummaryReport r)
    {
        var sb = new StringBuilder("service_line,amount,count\n");
        foreach (var row in r.ByServiceLine)
            sb.Append(CultureInfo.InvariantCulture, $"{Csv(row.ServiceLine)},{row.Amount.ToString(CultureInfo.InvariantCulture)},{row.Count}\n");
        return (sb.ToString(), r.ByServiceLine.Count);
    }

    private static (string, int) TatCsv(ApprovalTatReport r)
    {
        var sb = new StringBuilder("priority,count,avg_tat_seconds,p95_tat_seconds,sla_breaches\n");
        foreach (var row in r.ByPriority)
            sb.Append(CultureInfo.InvariantCulture, $"{Csv(row.Dimension)},{row.Count},{row.AvgTatSeconds.ToString(CultureInfo.InvariantCulture)},{row.P95TatSeconds.ToString(CultureInfo.InvariantCulture)},{row.SlaBreaches}\n");
        return (sb.ToString(), r.ByPriority.Count);
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}

/// <summary>Bundles the per-request report dependencies (gate + tenant + query/projector + clock/audit) so the
/// endpoints take one injected object, and centralizes the inline-or-async decision (NFR-006).</summary>
public sealed class ReportContext(
    ReportingGate gate, ReportingDbContext db, ReportQueries q, EventProjector projector,
    IHbmpPrincipalAccessor me, IAuditClient audit, TimeProvider clock,
    IBusinessCalendar calendar)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ReportingGate Gate { get; } = gate;
    public ReportingDbContext Db { get; } = db;
    public ReportQueries Q { get; } = q;
    public EventProjector Projector { get; } = projector;
    public IHbmpPrincipalAccessor Me { get; } = me;
    public IAuditClient Audit { get; } = audit;
    public TimeProvider Clock { get; } = clock;
    /// <summary>18.A3 — Africa/Cairo business dates; never derive a date from Clock directly.</summary>
    public IBusinessCalendar Calendar { get; } = calendar;

    public string Tenant => Me.Principal?.TenantId ?? "";

    /// <summary>Authorize, parse the range, then either run the report inline (operational budget) or — for a long
    /// range — persist a job, compute it, and return the handle (202) the client polls.</summary>
    public async Task<IResult> RunOrQueue<T>(string action, string report, string? from, string? to,
        Func<DateOnly, DateOnly, Task<T>> run, CancellationToken ct)
    {
        var denied = await Gate.CheckAsync(action, ct);
        if (denied is not null) return denied;

        var (f, t) = Api.Period.Parse(from, to, Calendar);
        if (!Api.Period.IsLongRange(f, t))
            return Results.Ok(await run(f, t));

        var job = new ReportJob { TenantId = Tenant, Report = report, Status = "Running", CreatedAt = Clock.GetUtcNow() };
        Db.ReportJobs.Add(job);
        await Db.SaveChangesAsync(ct);
        var result = await run(f, t);                 // computed now; a real heavy job would run on a worker
        job.Status = "Complete"; job.ProgressPercent = 100; job.CompletedAt = Clock.GetUtcNow();
        job.ResultJson = JsonSerializer.Serialize(result, Json);
        await Db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/v1/reports/jobs/{job.JobId}",
            new JobHandleView(job.JobId, job.Status, job.ProgressPercent, $"/api/v1/reports/jobs/{job.JobId}"));
    }
}
