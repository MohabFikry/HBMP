using Mersal.Audit.Client;
using Mersal.Events;
using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// Phase 19.5b — the bulk pipeline: scan → parse → validate → dry-run → commit → reconcile → roll back.
///
/// <para><b>ONE TRANSACTION PER ROW.</b> Never one for the file. A 50 000-row job inside a single transaction
/// holds locks for minutes, blocks every reception desk trying to enrol somebody, and takes the whole file
/// down when row 49 000 fails. Per-row transactions cost more round trips and buy the property that actually
/// matters: a bad row fails alone.</para>
///
/// <para><b>PARTIAL FAILURE IS THE NORMAL OUTCOME</b>, and it is reported, not smoothed over. A job ends
/// Completed with an applied count and a failed count, and the failed rows carry the same reason codes the
/// single-member form would have given.</para>
/// </summary>
public sealed class BulkJobEngine(
    PolicyDbContext db,
    IBulkFileParser parser,
    IEnumerable<IBulkRowApplier> appliers,
    IOperationalDocumentStore documents,
    IAuditClient audit,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<BulkJobEngine> logger)
{
    /// <summary>How many row errors the API returns inline. The rest are in the downloadable report — an
    /// error list of 9 000 rows is not a response body, and the ones that are identified content do not belong
    /// in a JSON payload that ends up in a browser cache and a proxy log.</summary>
    public const int InlineErrorLimit = 50;

    private IBulkRowApplier ApplierFor(BulkJobType type) =>
        appliers.FirstOrDefault(a => a.JobType == type)
        ?? throw new InvalidOperationException($"No applier is registered for {type}.");

    /// <summary>Bind the job's stored batch defaults onto the caller's scope. The scope carries who and where;
    /// the job carries what the batch is for, and only the job can, because upload is where it was stated.</summary>
    private static BulkScope WithJobDefaults(BulkScope scope, BulkJob job)
    {
        var defaults = new BulkJobDefaults(job.DefaultPlanId, job.DefaultNetworkTierId, job.DefaultBranchId);
        if (!defaults.Any) return scope;
        return new BulkScope
        {
            Actor = scope.Actor,
            BearerToken = scope.BearerToken,
            Payers = scope.Payers,
            PermittedBranchIds = scope.PermittedBranchIds,
            ActiveBranchId = scope.ActiveBranchId,
            MaySupervise = scope.MaySupervise,
            Defaults = defaults,
        };
    }

    // ---- 1. Upload: scan first, then parse ---------------------------------------------------------------

    /// <summary>
    /// Store the file (behind document-service's fail-closed scan), parse it against the template, and stage
    /// every row. Nothing is applied and nothing is validated yet.
    /// </summary>
    public async Task<BulkJob> UploadAsync(
        BulkJobType jobType, string fileName, string contentType, byte[] content,
        ActorRef actor, string? actorUsername, string? bearerToken,
        BulkJobDefaults? defaults = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var now = clock.GetUtcNow();
        var job = new BulkJob
        {
            JobId = Guid.NewGuid(), JobType = jobType, FileName = fileName,
            BatchId = Guid.NewGuid(), Status = BulkJobStatus.Scanning,
            SubmittedByUserId = actor.UserId, SubmittedByUsername = actorUsername, SubmittedAt = now,
            // Captured at UPLOAD, so the dry run and the commit apply the same ones.
            DefaultPlanId = defaults?.PlanId,
            DefaultNetworkTierId = defaults?.NetworkTierId,
            DefaultBranchId = defaults?.BranchId,
            CreatedAt = now, UpdatedAt = now, CreatedBy = actor.UserId, UpdatedBy = actor.UserId,
        };
        db.BulkJobs.Add(job);
        await db.SaveChangesAsync(ct);

        try
        {
            job.FileDocumentId = await documents.StoreAsync(
                nameof(BulkJobKinds.BulkUpload), job.JobId, fileName, contentType, content, bearerToken, ct);
        }
        catch (BulkFileInfectedException infected)
        {
            // FAIL AT SCANNING, terminally. Nothing is parsed, no row is created, and the status says exactly
            // which gate stopped it — an operator who reads "upload failed" retries; one who reads "the file
            // was quarantined" calls somebody.
            job.Status = BulkJobStatus.Failed;
            job.FailureCode = "FILE_INFECTED";
            job.FailureDetail = infected.Signature;
            job.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await AuditJob(job, AuditAction.Create, "infected", AuditSeverity.High, ct);
            return job;
        }

        if (job.FileDocumentId is null)
        {
            // Could not store means could not SCAN. A file that was not scanned is not parsed — the whole
            // point of the fail-closed rule is that "the scanner was unavailable" and "the file is clean" are
            // not the same answer.
            job.Status = BulkJobStatus.Failed;
            job.FailureCode = "SCAN_UNAVAILABLE";
            job.FailureDetail = "document-service could not be reached, so the file could not be scanned or stored. " +
                                "Nothing was parsed; retry the upload.";
            job.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await AuditJob(job, AuditAction.Create, "scan-unavailable", AuditSeverity.Warning, ct);
            return job;
        }

        var template = BulkTemplates.For(jobType);
        var parsed = parser.Parse(template, fileName, content);
        if (!parsed.Ok)
        {
            job.Status = BulkJobStatus.Failed;
            job.FailureCode = parsed.Failure!.Code;
            job.FailureDetail = parsed.Failure.DetailEn;
            job.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await AuditJob(job, AuditAction.Create, "parse-failed", AuditSeverity.Notice, ct);
            return job;
        }

        foreach (var row in parsed.Rows)
        {
            db.BulkJobRows.Add(new BulkJobRow
            {
                RowId = Guid.NewGuid(), JobId = job.JobId, RowNumber = row.RowNumber,
                Raw = BulkSnapshots.Write(row.Cells), Status = BulkRowStatus.Valid, CreatedAt = now,
            });
        }
        job.TotalRows = parsed.Rows.Count;
        job.ValidRows = parsed.Rows.Count;   // provisional until validation runs
        job.Status = BulkJobStatus.Uploaded;
        job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        await AuditJob(job, AuditAction.Create, $"uploaded;rows={job.TotalRows}", AuditSeverity.Info, ct);
        return job;
    }

    // ---- 2. Validate + dry run ---------------------------------------------------------------------------

    /// <summary>
    /// Check every row INDEPENDENTLY and record the outcome. Nothing is applied.
    ///
    /// <para>Independent means one row's failure never stops another row being checked: the file's whole point
    /// is to tell an operator everything that is wrong with it in one pass. A validator that stopped at the
    /// first error would turn a 37-error file into 37 upload cycles.</para>
    /// </summary>
    public async Task<BulkValidationReport> ValidateAsync(
        Guid jobId, BulkScope scope, string? bearerToken, CancellationToken ct = default)
    {
        var job = await db.BulkJobs.FirstOrDefaultAsync(j => j.JobId == jobId, ct)
                  ?? throw new InvalidOperationException($"No bulk job {jobId}.");
        if (!BulkJobTransitions.MayValidate(job.Status))
            return BulkValidationReport.Refused(job, $"A {job.Status} job cannot be validated.");

        // The batch defaults come off the JOB, not off this request — so the dry run models what the commit
        // will do rather than whatever the client happened to send this time.
        scope = WithJobDefaults(scope, job);
        var applier = ApplierFor(job.JobType);
        var rows = await db.BulkJobRows.Where(r => r.JobId == jobId).OrderBy(r => r.RowNumber).ToListAsync(ct);

        job.Status = BulkJobStatus.Validating;
        await db.SaveChangesAsync(ct);

        var errors = new List<BulkRowError>();
        var previews = new List<BulkRowPreview>();
        // How many rows WOULD change, as against how many of them fit in the inline list. Counted separately
        // from `valid` because a row can be valid and produce no preview — one already applied by an earlier
        // commit is left alone, and an applier that offers no diff for a row still counts it valid.
        var previewable = 0;
        var valid = 0;

        foreach (var row in rows)
        {
            // Rows already APPLIED by a previous commit are left alone: re-validating them would report
            // "already enrolled" as an error on a row that succeeded.
            if (row.Status == BulkRowStatus.Applied) { valid++; continue; }

            var parsed = ToParsedRow(row);
            var outcome = await applier.ValidateAsync(parsed, scope, ct);
            switch (outcome)
            {
                case RowOutcome.Valid v:
                    row.Status = BulkRowStatus.Valid;
                    row.Normalized = BulkSnapshots.Write(v.Normalized);
                    row.ErrorCode = null; row.ErrorDetail = null; row.ErrorDetailAr = null;
                    valid++;
                    previewable++;
                    if (previews.Count < InlineErrorLimit)
                        previews.Add(new BulkRowPreview(row.RowNumber, v.Preview.SummaryEn, v.Preview.SummaryAr, v.Preview.Changes));
                    break;

                case RowOutcome.Invalid i:
                    row.Status = BulkRowStatus.Invalid;
                    row.ErrorCode = i.Error.Code;
                    row.ErrorDetail = i.Error.DetailEn;
                    row.ErrorDetailAr = i.Error.DetailAr;
                    errors.Add(new BulkRowError(row.RowNumber, i.Error.Code, i.Error.DetailEn, i.Error.DetailAr));
                    break;

                default:
                    row.Status = BulkRowStatus.Valid;
                    valid++;
                    break;
            }
        }

        job.ValidRows = valid;
        job.InvalidRows = rows.Count - valid;
        job.Status = BulkJobStatus.Validated;
        job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // The full error list goes to a stored, downloadable file — it names people. Only the first N come back
        // inline, and only enough of each to fix the row.
        if (errors.Count > 0)
            job.ErrorDocumentId = await StoreErrorReportAsync(job, errors, bearerToken, ct);
        await db.SaveChangesAsync(ct);

        await AuditJob(job, AuditAction.Decision,
            $"validated;valid={job.ValidRows};invalid={job.InvalidRows}", AuditSeverity.Info, ct);

        return new BulkValidationReport(
            job, [.. errors.Take(InlineErrorLimit)], previews, errors.Count, previewable, null);
    }

    // ---- 3. Commit ---------------------------------------------------------------------------------------

    /// <summary>
    /// Apply every Valid row, each in its OWN transaction, each with an idempotency key derived from
    /// (job, row).
    ///
    /// <para>A row that fails is marked Failed and the loop CONTINUES. The alternative — abort on first
    /// failure — leaves the job half-applied with no record of where it stopped, which is the worst of both
    /// outcomes: not atomic, and not accounted for either.</para>
    /// </summary>
    public async Task<BulkCommitReport> CommitAsync(
        Guid jobId, BulkScope scope, string? bearerToken, CancellationToken ct = default)
    {
        var job = await db.BulkJobs.FirstOrDefaultAsync(j => j.JobId == jobId, ct)
                  ?? throw new InvalidOperationException($"No bulk job {jobId}.");
        if (!BulkJobTransitions.MayCommit(job.Status))
            return BulkCommitReport.Refused(job,
                job.Status == BulkJobStatus.Completed
                    ? "This job has already been committed. Re-committing is safe (rows are idempotent by job and row number) but must be started from a validated job."
                    : $"A {job.Status} job cannot be committed — validate it first.");

        scope = WithJobDefaults(scope, job);
        var applier = ApplierFor(job.JobType);
        var rowIds = await db.BulkJobRows
            .Where(r => r.JobId == jobId && (r.Status == BulkRowStatus.Valid || r.Status == BulkRowStatus.Failed))
            .OrderBy(r => r.RowNumber).Select(r => r.RowId).ToListAsync(ct);

        job.Status = BulkJobStatus.Committing;
        await db.SaveChangesAsync(ct);

        var applied = 0;
        var failed = 0;
        var skipped = 0;
        var errors = new List<BulkRowError>();

        foreach (var rowId in rowIds)
        {
            ct.ThrowIfCancellationRequested();
            var row = await db.BulkJobRows.FirstOrDefaultAsync(r => r.RowId == rowId, ct);
            if (row is null) continue;

            var parsed = ToParsedRow(row);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var outcome = await applier.ApplyAsync(parsed, scope, job.JobId, row.RowNumber, ct);
                switch (outcome)
                {
                    case RowOutcome.Applied a:
                        row.Status = BulkRowStatus.Applied;
                        row.TargetRef = a.TargetRef;
                        row.BeforeSnapshot = a.Before is null ? null : BulkSnapshots.Write(a.Before);
                        row.AppliedAt = clock.GetUtcNow();
                        row.ErrorCode = null; row.ErrorDetail = null; row.ErrorDetailAr = null;
                        applied++;
                        // The thread back from a member record to the upload that created it. Per applied row,
                        // deliberately: "a bulk job ran" cannot answer "where did this membership come from".
                        await audit.EmitAsync(new AuditEventDraft
                        {
                            EntityType = "bulk_job_row", EntityId = $"{job.JobId}:{row.RowNumber}",
                            Action = AuditAction.Create, ActorUserId = scope.Actor.Subject,
                            DecisionOutcome = $"applied;target={a.TargetRef}",
                            DecisionReasonCode = job.JobType.ToString(),
                        }, ct);
                        break;

                    case RowOutcome.Skipped s:
                        row.Status = BulkRowStatus.Skipped;
                        row.ErrorCode = "ALREADY_APPLIED";
                        row.ErrorDetail = s.Reason;
                        skipped++;
                        break;

                    case RowOutcome.Invalid i:
                        row.Status = BulkRowStatus.Failed;
                        row.ErrorCode = i.Error.Code;
                        row.ErrorDetail = i.Error.DetailEn;
                        row.ErrorDetailAr = i.Error.DetailAr;
                        failed++;
                        errors.Add(new BulkRowError(row.RowNumber, i.Error.Code, i.Error.DetailEn, i.Error.DetailAr));
                        break;

                    default:
                        row.Status = BulkRowStatus.Failed;
                        failed++;
                        break;
                }

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await tx.RollbackAsync(ct);
                // The row's own state must still be recorded, OUTSIDE the transaction that failed — otherwise
                // a crashed row is indistinguishable from one that was never reached.
                db.ChangeTracker.Clear();
                logger.LogError(ex, "bulk job {JobId} row {RowNumber} failed", job.JobId, row.RowNumber);
                // The INNERMOST message. "An error occurred while saving the entity changes" tells an operator
                // reading the error report nothing at all; the constraint name underneath it tells them what
                // to fix.
                var detail = Innermost(ex).Message;
                await db.BulkJobRows.Where(r => r.RowId == rowId).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, BulkRowStatus.Failed)
                    .SetProperty(r => r.ErrorCode, "ROW_FAILED")
                    .SetProperty(r => r.ErrorDetail, detail), ct);
                failed++;
                errors.Add(new BulkRowError(row.RowNumber, "ROW_FAILED", detail, detail));
            }
            finally
            {
                // A 50 000-row job would otherwise accumulate 50 000 tracked graphs in one scoped context.
                db.ChangeTracker.Clear();
            }
        }

        // The rows above each committed in their own transaction — that is deliberate, so one bad row does not
        // undo 49 999 good ones. What follows is a single fact, "this job is finished and here is its tally",
        // and it commits with the event that announces it. Marking the job Completed without emitting
        // BulkJobCompleted would leave every downstream watcher waiting on a job that already ended.
        await using var finishTx = await db.Database.BeginTransactionAsync(ct);
        job = await db.BulkJobs.FirstAsync(j => j.JobId == jobId, ct);
        job.AppliedRows = applied;
        job.FailedRows = failed;
        job.SkippedRows = skipped;
        job.Status = BulkJobStatus.Completed;
        job.CompletedAt = clock.GetUtcNow();
        job.UpdatedAt = job.CompletedAt.Value;
        await db.SaveChangesAsync(ct);

        if (errors.Count > 0)
        {
            job.ErrorDocumentId = await StoreErrorReportAsync(job, errors, bearerToken, ct);
            await db.SaveChangesAsync(ct);
        }

        await AuditJob(job, AuditAction.Create,
            $"committed;applied={applied};failed={failed};skipped={skipped}", AuditSeverity.Notice, ct);
        await outbox.EnqueueAsync("BulkJobCompleted", "policy.events", new
        {
            tenantId = job.TenantId, jobId = job.JobId, jobType = job.JobType.ToString(),
            batchId = job.BatchId, total = job.TotalRows, valid = job.ValidRows,
            applied, failed, skipped,
        }, ct);
        await finishTx.CommitAsync(ct);

        return new BulkCommitReport(job, [.. errors.Take(InlineErrorLimit)], errors.Count, null);
    }

    // ---- 4. Roll back by batch ---------------------------------------------------------------------------

    /// <summary>
    /// Reverse every APPLIED row of a job, one row at a time, refusing the ones that are no longer reversible
    /// and saying why.
    ///
    /// <para>Refusal is PER ROW, not per job. A file of 500 enrolments where three members have since consumed
    /// benefit is 497 rows that can be cleanly reversed and three that need a human decision — and refusing all
    /// 500 because of the three would leave the operator with no path at all.</para>
    /// </summary>
    public async Task<BulkRollbackReport> RollBackAsync(
        Guid jobId, BulkScope scope, string reason, string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var job = await db.BulkJobs.FirstOrDefaultAsync(j => j.JobId == jobId, ct)
                  ?? throw new InvalidOperationException($"No bulk job {jobId}.");
        if (!BulkJobTransitions.MayRollBack(job.Status))
            return BulkRollbackReport.Refused(job, $"A {job.Status} job cannot be rolled back.");

        var applier = ApplierFor(job.JobType);
        if (!applier.IsReversible)
            return BulkRollbackReport.Refused(job, $"{job.JobType} jobs cannot be reversed automatically.");

        var rowIds = await db.BulkJobRows
            .Where(r => r.JobId == jobId && r.Status == BulkRowStatus.Applied)
            .OrderByDescending(r => r.RowNumber)   // reverse order: undo the last change first
            .Select(r => r.RowId).ToListAsync(ct);

        var reversed = 0;
        var refused = new List<BulkRowError>();

        foreach (var rowId in rowIds)
        {
            ct.ThrowIfCancellationRequested();
            var row = await db.BulkJobRows.FirstOrDefaultAsync(r => r.RowId == rowId, ct);
            if (row is null) continue;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var outcome = await applier.ReverseAsync(row, scope, ct);
                if (outcome is RowOutcome.Invalid invalid)
                {
                    refused.Add(new BulkRowError(row.RowNumber, invalid.Error.Code, invalid.Error.DetailEn, invalid.Error.DetailAr));
                    row.ErrorCode = invalid.Error.Code;
                    row.ErrorDetail = invalid.Error.DetailEn;
                    row.ErrorDetailAr = invalid.Error.DetailAr;
                }
                else
                {
                    // The row stays APPLIED-then-reversed in the audit trail; its status becomes Skipped so a
                    // second rollback does not try again. The row itself is never deleted — the record that
                    // this line was uploaded and applied is the point.
                    row.Status = BulkRowStatus.Skipped;
                    row.ErrorCode = "ROLLED_BACK";
                    row.ErrorDetail = reason;
                    reversed++;
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "bulk_job_row", EntityId = $"{job.JobId}:{row.RowNumber}",
                        Action = AuditAction.StateChange, ActorUserId = scope.Actor.Subject,
                        DecisionOutcome = "rolled-back", DecisionReasonCode = reason,
                        Severity = AuditSeverity.Notice,
                    }, ct);
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                logger.LogError(ex, "rollback of bulk job {JobId} row {RowNumber} failed", job.JobId, row.RowNumber);
                refused.Add(new BulkRowError(row.RowNumber, "ROLLBACK_FAILED", ex.Message, ex.Message));
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        job = await db.BulkJobs.FirstAsync(j => j.JobId == jobId, ct);
        // RolledBack only when EVERY applied row came back. A partial rollback that reported itself as complete
        // would be the most dangerous state in this file: an operator would believe the file had been undone.
        job.Status = refused.Count == 0 ? BulkJobStatus.RolledBack : BulkJobStatus.Completed;
        job.RolledBackAt = clock.GetUtcNow();
        job.RolledBackBy = scope.Actor.UserId;
        job.AppliedRows = Math.Max(job.AppliedRows - reversed, 0);
        job.UpdatedAt = job.RolledBackAt.Value;
        await db.SaveChangesAsync(ct);

        await AuditJob(job, AuditAction.StateChange,
            $"rolled-back;reversed={reversed};refused={refused.Count}", AuditSeverity.High, ct);
        await outbox.EnqueueAsync("BulkJobRolledBack", "policy.events", new
        {
            tenantId = job.TenantId, jobId = job.JobId, batchId = job.BatchId,
            reversed, refused = refused.Count, reason,
        }, ct);

        return new BulkRollbackReport(job, reversed, refused, null);
    }

    // ---- 5. Reconcile ------------------------------------------------------------------------------------

    /// <summary>Submitted vs valid vs applied vs failed vs skipped, and whether the arithmetic closes. The
    /// migration toolkit's <c>ReconciliationReport.Balances</c>, applied to a job rather than a stream.</summary>
    public async Task<BulkReconciliation> ReconcileAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.BulkJobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == jobId, ct)
                  ?? throw new InvalidOperationException($"No bulk job {jobId}.");
        var counts = await db.BulkJobRows.AsNoTracking()
            .Where(r => r.JobId == jobId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int CountOf(BulkRowStatus status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

        return new BulkReconciliation(
            job.JobId, job.JobType.ToString(), job.Status.ToString(), job.BatchId,
            job.TotalRows, CountOf(BulkRowStatus.Valid), CountOf(BulkRowStatus.Invalid),
            CountOf(BulkRowStatus.Applied), CountOf(BulkRowStatus.Failed), CountOf(BulkRowStatus.Skipped),
            job.Balances, job.ErrorDocumentId);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex;
    }

    private static ParsedRow ToParsedRow(BulkJobRow row)
    {
        var cells = BulkSnapshots.Read(row.Raw) ?? new Dictionary<string, object?>();
        return new ParsedRow(row.RowNumber,
            cells.ToDictionary(c => c.Key, c => c.Value?.ToString(), StringComparer.Ordinal));
    }

    private async Task<Guid?> StoreErrorReportAsync(
        BulkJob job, IReadOnlyList<BulkRowError> errors, string? bearerToken, CancellationToken ct)
    {
        var bytes = BulkCsv.Write(
            ["row_number", "error_code", "detail_en", "detail_ar"],
            errors.Select(e => new List<string?>
            {
                e.RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                e.Code, e.DetailEn, e.DetailAr,
            }));

        var documentId = await documents.StoreAsync(
            nameof(BulkJobKinds.BulkErrorReport), job.JobId,
            $"errors-{job.JobId:N}.csv", "text/csv", bytes, bearerToken, ct);

        if (documentId is null)
            logger.LogWarning("bulk job {JobId}: the error report could not be stored; errors remain readable per row", job.JobId);
        return documentId;
    }

    private async Task AuditJob(BulkJob job, AuditAction action, string outcome, AuditSeverity severity, CancellationToken ct) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "bulk_job", EntityId = job.JobId.ToString(), Action = action,
            ActorUserId = job.SubmittedByUsername, TenantId = job.TenantId,
            DecisionOutcome = outcome, DecisionReasonCode = job.JobType.ToString(), Severity = severity,
        }, ct);
}

/// <summary>The document kinds this engine stores, matching document-service's enum by name.</summary>
internal enum BulkJobKinds { BulkUpload, BulkErrorReport, Extract }

public sealed record BulkRowError(int RowNumber, string Code, string DetailEn, string DetailAr);

public sealed record BulkRowPreview(
    int RowNumber, string SummaryEn, string SummaryAr, IReadOnlyDictionary<string, object?> Changes);

/// <summary>
/// The dry run's answer.
/// <para><c>Errors</c> and <c>Preview</c> are both capped at <see cref="BulkJobEngine.InlineErrorLimit"/> —
/// the full lists name people and belong in the stored report — so each travels with its REAL size beside
/// it. A caller that renders the capped list without the count shows fifty rows of a three-thousand-row
/// answer and says nothing, which reads as "this is all of them".</para>
/// </summary>
public sealed record BulkValidationReport(
    BulkJob Job, IReadOnlyList<BulkRowError> Errors, IReadOnlyList<BulkRowPreview> Preview,
    int TotalErrors, int TotalPreview, string? Refusal)
{
    public static BulkValidationReport Refused(BulkJob job, string reason) => new(job, [], [], 0, 0, reason);
}

public sealed record BulkCommitReport(BulkJob Job, IReadOnlyList<BulkRowError> Errors, int TotalErrors, string? Refusal)
{
    public static BulkCommitReport Refused(BulkJob job, string reason) => new(job, [], 0, reason);
}

public sealed record BulkRollbackReport(
    BulkJob Job, int Reversed, IReadOnlyList<BulkRowError> RefusedRows, string? Refusal)
{
    public static BulkRollbackReport Refused(BulkJob job, string reason) => new(job, 0, [], reason);
}

public sealed record BulkReconciliation(
    Guid JobId, string JobType, string Status, Guid BatchId,
    int Submitted, int Valid, int Invalid, int Applied, int Failed, int Skipped,
    bool Balances, Guid? ErrorDocumentId);
