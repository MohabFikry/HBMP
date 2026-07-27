namespace Mersal.Document.Domain;

// Document metadata per 15-database-erd §12. Blob BYTES live in object storage (MinIO/S3), never
// in the RDBMS — only metadata + checksum here.

public enum DocType { IDScan, Consent, Referral, LabResult, ImagingReport }
public enum Classification { PHI, PII, Internal }

public sealed class Document
{
    public Guid DocumentId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public DocType DocType { get; set; }
    public Guid OwnerBeneficiaryId { get; set; }          // logical FK
    public Classification Classification { get; set; }
    public string BlobContainer { get; set; } = default!;
    public int CurrentVersionNo { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<DocumentVersion> Versions { get; set; } = [];
}

/// <summary>What an operational document is FOR. Kept as an enum rather than free text because the retention
/// rule differs per kind — a bulk error report is short-lived working data, an extract is a record of a
/// disclosure — and a retention policy cannot be written against a string somebody typed.</summary>
public enum OperationalDocKind { BulkUpload, BulkErrorReport, Extract }

/// <summary>
/// Phase 19.5b — a file that belongs to an OPERATION rather than to a person: a bulk upload, its error report,
/// an extract.
///
/// <para>Deliberately not a <see cref="Document"/> with a null owner. Beneficiary documents are listed,
/// authorized and retained BY OWNER, and a file with no owner sitting in that table is one that every
/// owner-scoped query has to remember to exclude. It is also, frequently, a file about MANY people — a bulk
/// error report quotes hundreds of member numbers — so "whose document is this" has no answer that the
/// beneficiary model would accept.</para>
///
/// <para>It goes through the SAME upload pipeline: validation, checksum, fail-closed ClamAV, MinIO. A second
/// ingest path would be a second way for malware to arrive.</para>
/// </summary>
public sealed class OperationalDocument
{
    public Guid DocumentId { get; set; }
    public string TenantId { get; set; } = "";
    public OperationalDocKind Kind { get; set; }
    /// <summary>The job or run this file belongs to — a logical reference into the owning service.</summary>
    public Guid OwnerRef { get; set; }
    public string OwnerService { get; set; } = default!;
    public Classification Classification { get; set; } = Classification.PHI;
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = "text/csv";
    public string BlobPath { get; set; } = default!;
    public string ChecksumSha256 { get; set; } = default!;
    public long SizeBytes { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed class DocumentVersion
{
    public Guid DocumentVersionId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid DocumentId { get; set; }
    public int VersionNo { get; set; }
    public string BlobPath { get; set; } = default!;
    public string ChecksumSha256 { get; set; } = default!;
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public string? UploadedBy { get; set; }
}
