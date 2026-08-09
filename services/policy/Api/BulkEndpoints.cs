using System.Text;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.5b — the bulk upload surface (design 38 §4.4).
///
/// <para>Five verbs, in the order an operator uses them: get the template, upload, validate (dry run), commit,
/// reconcile — plus roll back. They are separate endpoints because the separation IS the safety property:
/// upload does not apply, validate does not apply, and only a Validated job can be committed.</para>
/// </summary>
public static class BulkEndpoints
{
    /// <summary>Refuse a file larger than this outright. A 25 MB CSV is roughly 200 000 enrolment rows —
    /// already above the parser's ceiling — so anything bigger is a mistake, and finding out before the bytes
    /// are read is cheaper for everybody.</summary>
    public const long MaxUploadBytes = 25 * 1024 * 1024;

    public static void MapBulkJobs(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1/bulk-jobs").RequireAuthorization(HbmpPolicies.Scope("policy:read"));
        var write = app.MapGroup("/api/v1/bulk-jobs").RequireAuthorization(HbmpPolicies.Scope("policy:write"));

        MapTemplates(app);
        MapUpload(write);
        MapLifecycle(write);
        MapReads(read);
    }

    // ---- Templates ---------------------------------------------------------------------------------------

    private static void MapTemplates(IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/api/v1/bulk-templates").RequireAuthorization(HbmpPolicies.Scope("policy:read"));

        templates.MapGet("", () => Results.Ok(BulkTemplates.All.Select(t => new BulkTemplateView(
            t.JobType.ToString(), t.PurposeEn, t.PurposeAr,
            [.. t.Columns.Select(c => new BulkColumnView(c.Name, c.Kind.ToString(), c.Required, c.DescriptionEn, c.DescriptionAr))]))))
        .Produces<IEnumerable<BulkTemplateView>>();

        // The downloadable file. Handing an operator the exact headers is the cheapest defence there is against
        // the column mismatch that otherwise fails the whole job.
        templates.MapGet("/{jobType}", (string jobType) =>
        {
            if (!Enum.TryParse<BulkJobType>(jobType, ignoreCase: true, out var type))
                return ProblemResults.Invalid("UNKNOWN_JOB_TYPE", $"'{jobType}' is not a bulk job type.");
            var template = BulkTemplates.For(type);
            return Results.File(Encoding.UTF8.GetBytes(template.ToCsv()), "text/csv",
                $"{type.ToString().ToLowerInvariant()}-template.csv");
        });
    }

    // ---- Upload ------------------------------------------------------------------------------------------

    private static void MapUpload(RouteGroupBuilder write)
    {
        write.MapPost("", async (
            string jobType, IFormFile file, BulkJobEngine engine, PolicyGate gate,
            // Batch-level coverage, stated once for the whole file. Recorded on the JOB (0018) so the dry run
            // and the commit apply the same values; they fill a blank cell and never override a stated one.
            Guid? defaultPlanId, Guid? defaultNetworkTierId, Guid? defaultBranchId,
            HttpContext http, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            if (!Enum.TryParse<BulkJobType>(jobType, ignoreCase: true, out var type))
                return ProblemResults.Invalid("UNKNOWN_JOB_TYPE", $"'{jobType}' is not a bulk job type.");
            if (file is null || file.Length == 0)
                return ProblemResults.Invalid("NO_FILE", "No file was uploaded.");
            if (file.Length > MaxUploadBytes)
                return ProblemResults.Invalid("FILE_TOO_LARGE",
                    $"The file exceeds the {MaxUploadBytes / (1024 * 1024)} MB limit for a single job; split it.");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);

            var job = await engine.UploadAsync(
                type, file.FileName ?? "upload.csv", file.ContentType ?? "text/csv", ms.ToArray(),
                new ActorRef(gate.SubjectId, gate.Subject), gate.Subject,
                http.Request.Headers.Authorization.FirstOrDefault(),
                new BulkJobDefaults(defaultPlanId, defaultNetworkTierId, defaultBranchId), ct);

            // A job that failed at Scanning or parsing is returned with 200 and its terminal status rather than
            // as an error: the job RECORD exists, it is the answer to "what happened to my file", and it is
            // exactly what an operator needs to look at.
            return Results.Created($"/api/v1/bulk-jobs/{job.JobId}", BulkJobView.From(job));
        }).DisableAntiforgery()
        .Produces<BulkJobView>();
    }

    // ---- Validate / commit / roll back -------------------------------------------------------------------

    private static void MapLifecycle(RouteGroupBuilder write)
    {
        write.MapPost("/{id:guid}/validate", async (
            Guid id, BulkJobEngine engine, PolicyGate gate, IPayerDirectory payers, IBranchDirectory branches,
            HttpContext http, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var (scope, error) = await ScopeAsync(gate, payers, branches, http, ct);
            if (error is not null) return error;

            var report = await engine.ValidateAsync(id, scope!, http.Request.Headers.Authorization.FirstOrDefault(), ct);
            return report.Refusal is null
                ? Results.Ok(BulkValidationView.From(report))
                : ProblemResults.Conflict("NOT_VALIDATABLE", report.Refusal);
        })
        .Produces<BulkValidationView>();

        write.MapPost("/{id:guid}/commit", async (
            Guid id, BulkJobEngine engine, PolicyGate gate, IPayerDirectory payers, IBranchDirectory branches,
            HttpContext http, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            var (scope, error) = await ScopeAsync(gate, payers, branches, http, ct);
            if (error is not null) return error;

            var report = await engine.CommitAsync(id, scope!, http.Request.Headers.Authorization.FirstOrDefault(), ct);
            return report.Refusal is null
                ? Results.Ok(BulkCommitView.From(report))
                : ProblemResults.Conflict("NOT_COMMITTABLE", report.Refusal);
        })
        .Produces<BulkCommitView>();

        write.MapPost("/{id:guid}/rollback", async (
            Guid id, RollBackBulkJob req, BulkJobEngine engine, PolicyGate gate,
            IPayerDirectory payers, IBranchDirectory branches, HttpContext http, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Write, ct) is { } denied) return denied;
            // Reversing a bulk change is a SUPERVISORY act. Committing one file is one decision; undoing it
            // touches every row that succeeded, and frequently after somebody has already acted on them.
            if (await gate.CheckAsync(PolicyPolicies.Supervise, ct) is { } superviseDenied) return superviseDenied;
            if (req is null || string.IsNullOrWhiteSpace(req.Reason))
                return ProblemResults.Invalid("REASON_REQUIRED", "A reason is required to roll back a bulk job.");

            var (scope, error) = await ScopeAsync(gate, payers, branches, http, ct);
            if (error is not null) return error;

            var report = await engine.RollBackAsync(id, scope!, req.Reason, http.Request.Headers.Authorization.FirstOrDefault(), ct);
            return report.Refusal is null
                ? Results.Ok(BulkRollbackView.From(report))
                : ProblemResults.Conflict("NOT_ROLLBACKABLE", report.Refusal);
        })
        .Produces<BulkRollbackView>();
    }

    // ---- Reads -------------------------------------------------------------------------------------------

    private static void MapReads(RouteGroupBuilder read)
    {
        read.MapGet("", async (string? status, string? jobType, int? page, int? pageSize,
            PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var pageReq = PageRequest.Of(page, pageSize);
            var query = db.BulkJobs.AsNoTracking().AsQueryable();
            if (Enum.TryParse<BulkJobStatus>(status, ignoreCase: true, out var s)) query = query.Where(j => j.Status == s);
            if (Enum.TryParse<BulkJobType>(jobType, ignoreCase: true, out var t)) query = query.Where(j => j.JobType == t);

            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(j => j.SubmittedAt)
                .Skip(pageReq.Skip).Take(pageReq.PageSize).ToListAsync(ct);
            return Results.Ok(new { page = pageReq.Page, pageSize = pageReq.PageSize, total, items = rows.Select(BulkJobView.From) });
        });

        read.MapGet("/{id:guid}", async (Guid id, PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var job = await db.BulkJobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == id, ct);
            return job is null ? NotFound() : Results.Ok(BulkJobView.From(job));
        })
        .Produces<BulkJobView>();

        read.MapGet("/{id:guid}/reconciliation", async (Guid id, BulkJobEngine engine, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            return Results.Ok(await engine.ReconcileAsync(id, ct));
        });

        // The rows of a job, paginated. Row errors quote identifiers, so this is capped and audited exactly as
        // a member query is — and the FULL list lives in the stored error report, behind its own audited
        // download, rather than in a response body.
        read.MapGet("/{id:guid}/rows", async (Guid id, string? status, int? page, int? pageSize,
            PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var pageReq = PageRequest.Of(page, pageSize);
            var query = db.BulkJobRows.AsNoTracking().Where(r => r.JobId == id);
            if (Enum.TryParse<BulkRowStatus>(status, ignoreCase: true, out var s)) query = query.Where(r => r.Status == s);

            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(r => r.RowNumber)
                .Skip(pageReq.Skip).Take(pageReq.PageSize).ToListAsync(ct);
            return Results.Ok(new
            {
                page = pageReq.Page, pageSize = pageReq.PageSize, total,
                items = rows.Select(r => new BulkRowView(
                    r.RowNumber, r.Status.ToString(), r.ErrorCode, r.ErrorDetail, r.ErrorDetailAr, r.TargetRef, r.AppliedAt)),
            });
        })
        .Produces<IEnumerable<BulkRowView>>();
    }

    // ---- Scope resolution --------------------------------------------------------------------------------

    /// <summary>
    /// The scope every row is checked against: payer, branch and whether the caller may make retro-effective
    /// changes. Resolved ONCE per job rather than per row — the answer cannot change mid-job, and asking
    /// admin-service 10 000 times would make the directory the slowest part of the pipeline.
    /// </summary>
    private static async Task<(BulkScope? Scope, IResult? Error)> ScopeAsync(
        PolicyGate gate, IPayerDirectory payers, IBranchDirectory branches, HttpContext http, CancellationToken ct)
    {
        var principal = gate.Principal;
        if (principal is null) return (null, GateResults.Unauthenticated());

        var permitted = await payers.GetAsync(principal, ct);

        IReadOnlySet<Guid>? permittedBranches = null;
        Guid? activeBranch = null;
        if (BranchScopeModes.ModeFor(principal) == ScopeMode.BranchScoped)
        {
            var state = await BranchScopeResolver.ResolveAsync(
                principal, http.Request.Headers[BranchHeaders.ActiveBranch].FirstOrDefault(), branches, ct);
            if (state.Denied)
                return (null, GateResults.Forbidden("urn:hbmp:branch-scope-denied",
                    detail: "The requested active branch is not in your permitted set.", reason: "branch-not-permitted"));
            permittedBranches = state.Context.PermittedBranchIds;
            activeBranch = state.Context.ActiveBranchId;
        }

        var maySupervise = await gate.CheckAsync(PolicyPolicies.Supervise, ct) is null;

        return (new BulkScope
        {
            Actor = new ActorRef(gate.SubjectId, gate.Subject),
            BearerToken = http.Request.Headers.Authorization.FirstOrDefault(),
            Payers = permitted,
            PermittedBranchIds = permittedBranches,
            ActiveBranchId = activeBranch,
            MaySupervise = maySupervise,
        }, null);
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
}

public sealed record RollBackBulkJob(string Reason);

public sealed record BulkTemplateView(
    string JobType, string PurposeEn, string PurposeAr, IReadOnlyList<BulkColumnView> Columns);

public sealed record BulkColumnView(
    string Name, string Kind, bool Required, string DescriptionEn, string DescriptionAr);

public sealed record BulkJobView(
    Guid JobId, string JobType, string FileName, string Status, Guid BatchId,
    int TotalRows, int ValidRows, int InvalidRows, int AppliedRows, int FailedRows, int SkippedRows,
    bool Balances, string? FailureCode, string? FailureDetail,
    Guid? FileDocumentId, Guid? ErrorDocumentId,
    string? SubmittedBy, DateTimeOffset SubmittedAt, DateTimeOffset? CompletedAt, DateTimeOffset? RolledBackAt)
{
    public static BulkJobView From(BulkJob j)
    {
        ArgumentNullException.ThrowIfNull(j);
        return new BulkJobView(
            j.JobId, j.JobType.ToString(), j.FileName, j.Status.ToString(), j.BatchId,
            j.TotalRows, j.ValidRows, j.InvalidRows, j.AppliedRows, j.FailedRows, j.SkippedRows,
            j.Balances, j.FailureCode, j.FailureDetail, j.FileDocumentId, j.ErrorDocumentId,
            j.SubmittedByUsername, j.SubmittedAt, j.CompletedAt, j.RolledBackAt);
    }
}

public sealed record BulkRowView(
    int RowNumber, string Status, string? ErrorCode, string? ErrorDetail, string? ErrorDetailAr,
    Guid? TargetRef, DateTimeOffset? AppliedAt);

/// <summary>The dry run: counts, the first N errors, and a per-row diff of what WOULD change. The diff is the
/// part that earns the endpoint — counts alone tell an operator that 9 963 rows are valid, not that the file
/// is about to move everybody onto the wrong plan.</summary>
public sealed record BulkValidationView(
    BulkJobView Job, int TotalErrors, IReadOnlyList<BulkRowError> Errors,
    IReadOnlyList<BulkRowPreview> WouldChange, bool Committable)
{
    public static BulkValidationView From(BulkValidationReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new BulkValidationView(BulkJobView.From(r.Job), r.TotalErrors, r.Errors, r.Preview,
            BulkJobTransitions.MayCommit(r.Job.Status) && r.Job.ValidRows > 0);
    }
}

public sealed record BulkCommitView(BulkJobView Job, int TotalErrors, IReadOnlyList<BulkRowError> Errors)
{
    public static BulkCommitView From(BulkCommitReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new BulkCommitView(BulkJobView.From(r.Job), r.TotalErrors, r.Errors);
    }
}

public sealed record BulkRollbackView(
    BulkJobView Job, int Reversed, int Refused, IReadOnlyList<BulkRowError> RefusedRows)
{
    public static BulkRollbackView From(BulkRollbackReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new BulkRollbackView(BulkJobView.From(r.Job), r.Reversed, r.RefusedRows.Count, r.RefusedRows);
    }
}
