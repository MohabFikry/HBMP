using System.Globalization;

namespace Mersal.Policy.Domain;

// Phase 19.5b — bulk upload (design 38 §4.4). ONE engine for getting data in; the extract half lives in
// ExtractModel.cs.
//
// ============================================================================================================
// WHY A JOB IS A RECORD AND NOT A REQUEST
// ============================================================================================================
// A bulk file is the highest-leverage write in the platform: one upload can enrol ten thousand people, or
// terminate them. Three properties follow from that and they are the reason this is modelled as a persisted
// job with per-row state rather than a loop inside a request handler.
//
//  1. NOTHING IS APPLIED UNTIL SOMEBODY COMMITS. Validation is a separate, repeatable step whose output is a
//     dry-run diff. An operator who uploads the wrong file finds out from a preview, not from a support ticket.
//  2. EVERY ROW IS ACCOUNTED FOR. submitted = valid + invalid, and applied + failed + skipped = valid. A job
//     that cannot say what happened to a row is a job that quietly lost it.
//  3. A ROW IS THE UNIT OF WORK, NOT THE FILE. One transaction per row (never one for 50 000), so a bad row
//     fails alone and a resumed job cannot double-apply — the Idempotency-Key is derived from
//     (job_id, row_number), which is stable across every retry of the same file.

/// <summary>The job types that have a template and an applier. Each one maps to an EXISTING write path —
/// the bulk engine never invents a second way to change a membership.</summary>
public enum BulkJobType
{
    MemberEnrolment,
    MemberTermination,
    PlanChange,
    GroupAssignment,
    ContactUpdate,
    ProviderTierAssignment,
    /// <summary>Populates a DRAFT plan version only. An Active version is immutable by trigger (0005), and a
    /// bulk path into a live benefit configuration would be a way to change what thousands of people are
    /// entitled to without the amend→new-version→activate review that exists for exactly that reason.</summary>
    BenefitRuleImport,
}

/// <summary>
/// The job's lifecycle. Scanning is its own state rather than a step inside upload because an infected file
/// must be a VISIBLE terminal outcome — "Failed at Scanning" tells an operator to stop and call somebody;
/// "upload failed" tells them to try again.
/// </summary>
public enum BulkJobStatus
{
    Uploaded,
    Scanning,
    Validating,
    Validated,
    Committing,
    Completed,
    Failed,
    RolledBack,
}

/// <summary>
/// Per-row state. <c>Skipped</c> is distinct from <c>Failed</c>: a row that was already applied by an earlier
/// run of the same job was skipped on purpose, and collapsing the two would make an idempotent re-commit look
/// like a partial failure every time.
/// </summary>
public enum BulkRowStatus { Valid, Invalid, Applied, Skipped, Failed }

public static class BulkJobTransitions
{
    /// <summary>A job is committable ONLY from Validated. Not from Uploaded (nothing has been checked), not
    /// from Completed (that is what the per-row idempotency key is for), and not from Failed.</summary>
    public static bool MayCommit(BulkJobStatus status) => status == BulkJobStatus.Validated;

    /// <summary>Re-validating is allowed from any non-terminal state — a referential error (an unknown group
    /// code) is frequently fixed in the SYSTEM rather than in the file, and forcing a re-upload would mean
    /// re-scanning and re-numbering rows that are already correct.</summary>
    public static bool MayValidate(BulkJobStatus status) =>
        status is BulkJobStatus.Uploaded or BulkJobStatus.Validating or BulkJobStatus.Validated;

    /// <summary>Only a job that actually applied something can be rolled back. Completed covers
    /// completed-with-errors, which is the normal case.</summary>
    public static bool MayRollBack(BulkJobStatus status) => status == BulkJobStatus.Completed;
}

/// <summary>
/// The idempotency key a bulk-applied row writes with.
///
/// <para>Derived from (job_id, row_number) and NOTHING else. It must be stable across a resumed job, a retried
/// commit and a re-run after a crash — so it cannot contain a timestamp, a GUID, or anything about the row's
/// content. Row 4 231 of job X is row 4 231 of job X forever, and the unique index on
/// <c>enrollment.idempotency_key</c> is what turns that into "cannot double-apply".</para>
/// </summary>
public static class BulkIdempotency
{
    public static string KeyFor(Guid jobId, int rowNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"bulk:{jobId:N}:{rowNumber}");
}

/// <summary>
/// A row-level error: a machine code plus a human sentence in BOTH locales.
///
/// <para>Bilingual is not decoration. The people who fix these files work in Arabic; an English-only error
/// file means the correction is guessed from a code, and a guessed correction to an enrolment file is a
/// person's cover.</para>
/// </summary>
public sealed record RowError(string Code, string DetailEn, string DetailAr)
{
    public static RowError MissingColumn(string column) => new(
        "MISSING_VALUE",
        $"'{column}' is required and was empty.",
        $"الحقل '{column}' مطلوب وكان فارغًا.");

    public static RowError BadFormat(string column, string expected) => new(
        "BAD_FORMAT",
        $"'{column}' is not a valid {expected}.",
        $"القيمة في '{column}' ليست {expected} صالحة.");

    public static RowError Unknown(string what, string value) => new(
        "UNKNOWN_REFERENCE",
        $"{what} '{value}' does not exist.",
        $"{what} '{value}' غير موجود.");

    public static RowError Rule(string code, string en, string ar) => new(code, en, ar);

    public static RowError OutOfScope(string what) => new(
        "OUT_OF_SCOPE",
        $"You are not permitted to change {what}. A bulk file cannot reach outside your own scope.",
        $"غير مصرح لك بتعديل {what}. لا يمكن استخدام ملف جماعي لتجاوز نطاق صلاحياتك.");
}

/// <summary>
/// A bulk job. <see cref="BatchId"/> is the reversibility boundary, borrowed wholesale from the migration
/// toolkit's <c>MigrationBatch</c> (phase 12.1): every row this job applied carries it, and rollback-by-batch
/// reverses exactly those rows and nothing pre-existing.
/// </summary>
public sealed class BulkJob
{
    public Guid JobId { get; set; }
    public string TenantId { get; set; } = "";
    public BulkJobType JobType { get; set; }
    public string FileName { get; set; } = default!;

    /// <summary>document-service's id for the uploaded file. The bytes live in MinIO behind the scan that
    /// cleared them; policy-service holds the reference and the row-level meaning, never the blob.</summary>
    public Guid? FileDocumentId { get; set; }

    /// <summary>The PHI-BEARING error report, stored the same way for the same reason: row errors quote member
    /// numbers and identifiers, so the report is a disclosure and gets an authorized, audited download rather
    /// than an inline body or a log line.</summary>
    public Guid? ErrorDocumentId { get; set; }

    public BulkJobStatus Status { get; set; } = BulkJobStatus.Uploaded;
    public string? FailureCode { get; set; }
    public string? FailureDetail { get; set; }

    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public int AppliedRows { get; set; }
    public int FailedRows { get; set; }
    public int SkippedRows { get; set; }

    public Guid BatchId { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedByUsername { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RolledBackAt { get; set; }
    public Guid? RolledBackBy { get; set; }

    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Reconciliation balances when every submitted row is accounted for. Mirrors
    /// <c>ReconciliationReport.Balances</c> in the migration toolkit — a load is not "done" until it does.</summary>
    public bool Balances => TotalRows == ValidRows + InvalidRows
                            && (Status != BulkJobStatus.Completed || ValidRows == AppliedRows + FailedRows + SkippedRows);
}

/// <summary>
/// APPEND-ONLY, one per parsed line. <see cref="Raw"/> is what the file said; <see cref="Normalized"/> is what
/// validation made of it. Keeping both is what lets a disputed row be answered with "this is the line you
/// uploaded" rather than with the system's interpretation of it.
/// </summary>
public sealed class BulkJobRow
{
    public Guid RowId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid JobId { get; set; }
    public int RowNumber { get; set; }

    public string Raw { get; set; } = "{}";
    public string? Normalized { get; set; }

    public BulkRowStatus Status { get; set; } = BulkRowStatus.Valid;
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public string? ErrorDetailAr { get; set; }

    /// <summary>The entity this row created or changed — the thread back from a member record to the upload
    /// that produced it, and what rollback-by-batch walks.</summary>
    public Guid? TargetRef { get; set; }

    /// <summary>What the target looked like BEFORE this row changed it, as jsonb. Rollback is a compensating
    /// change back to this, not a delete — the row it reverses may have updated something that existed long
    /// before the job.</summary>
    public string? BeforeSnapshot { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
