using Mersal.Document.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Document.Infrastructure;

public sealed class DocumentDbContext(DbContextOptions<DocumentDbContext> options) : DbContext(options)
{
    public const string Schema = "document";
    public DbSet<Document.Domain.Document> Documents => Set<Document.Domain.Document>();
    public DbSet<DocumentVersion> Versions => Set<DocumentVersion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("document");
        b.HasDefaultSchema(Schema);
        b.Entity<Document.Domain.Document>(e =>
        {
            e.ToTable("document");
            e.HasKey(x => x.DocumentId);
            e.Property(x => x.DocType).HasConversion<string>().HasColumnName("doc_type");
            e.Property(x => x.OwnerBeneficiaryId).HasColumnName("owner_beneficiary_id");
            e.Property(x => x.Classification).HasConversion<string>().HasColumnName("classification");
            e.Property(x => x.BlobContainer).HasColumnName("blob_container");
            e.Property(x => x.CurrentVersionNo).HasColumnName("current_version_no");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.DocumentId);
            e.HasIndex(x => x.OwnerBeneficiaryId);
        });
        b.Entity<DocumentVersion>(e =>
        {
            e.ToTable("document_version");
            e.HasKey(x => x.DocumentVersionId);
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.VersionNo).HasColumnName("version_no");
            e.Property(x => x.BlobPath).HasColumnName("blob_path");
            e.Property(x => x.ChecksumSha256).HasColumnName("checksum_sha256");
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            e.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
            e.HasIndex(x => new { x.DocumentId, x.VersionNo }).IsUnique();
        });

        // 19.5b — files that belong to an operation rather than to a person.
        b.Entity<OperationalDocument>(e =>
        {
            e.ToTable("operational_document");
            e.HasKey(x => x.DocumentId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Kind).HasConversion<string>().HasColumnName("kind");
            e.Property(x => x.OwnerRef).HasColumnName("owner_ref");
            e.Property(x => x.OwnerService).HasColumnName("owner_service");
            e.Property(x => x.Classification).HasConversion<string>().HasColumnName("classification");
            e.Property(x => x.FileName).HasColumnName("file_name");
            e.Property(x => x.ContentType).HasColumnName("content_type");
            e.Property(x => x.BlobPath).HasColumnName("blob_path");
            e.Property(x => x.ChecksumSha256).HasColumnName("checksum_sha256");
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.HasIndex(x => new { x.OwnerService, x.OwnerRef });
        });
    }

    public DbSet<OperationalDocument> OperationalDocuments => Set<OperationalDocument>();
}
