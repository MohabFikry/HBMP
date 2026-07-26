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
