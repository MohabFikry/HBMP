using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Claims.Infrastructure;

/// <summary>EF Core context for the <c>claims</c> schema (Phase 10b). Owns the schema exclusively — it never reads
/// another service's tables; cross-context data (tariffs, authorizations, eligibility) comes over the API/events.
/// The schema carries NO clinical column by design (22 §10A minimum-necessary note).</summary>
public sealed class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : DbContext(options)
{
    public const string Schema = "claims";

    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimLine> ClaimLines => Set<ClaimLine>();
    public DbSet<ClaimBatch> ClaimBatches => Set<ClaimBatch>();
    public DbSet<ClaimBatchItem> ClaimBatchItems => Set<ClaimBatchItem>();
    public DbSet<ClaimDecision> ClaimDecisions => Set<ClaimDecision>();
    public DbSet<ClaimAdjustment> ClaimAdjustments => Set<ClaimAdjustment>();
    public DbSet<ClaimSubmission> ClaimSubmissions => Set<ClaimSubmission>();
    public DbSet<ClaimSubmissionLine> ClaimSubmissionLines => Set<ClaimSubmissionLine>();
    public DbSet<ClaimDocument> ClaimDocuments => Set<ClaimDocument>();
    public DbSet<ReimbursementRequest> ReimbursementRequests => Set<ReimbursementRequest>();
    public DbSet<OcrExtraction> OcrExtractions => Set<OcrExtraction>();
    public DbSet<SettlementAdvice> SettlementAdvices => Set<SettlementAdvice>();
    public DbSet<SettlementPaymentReference> SettlementPaymentReferences => Set<SettlementPaymentReference>();
    public DbSet<ClaimAppeal> ClaimAppeals => Set<ClaimAppeal>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("claims");
        b.HasDefaultSchema(Schema);

        b.Entity<Claim>(e =>
        {
            e.ToTable("claim");
            e.HasKey(x => x.ClaimId);
            e.Property(x => x.Origin).HasConversion<string>().HasColumnName("origin");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.ClaimNo).IsUnique();
            e.HasIndex(x => new { x.BeneficiaryId, x.Status });
            e.HasIndex(x => new { x.ProviderId, x.ServiceDateFrom });
            e.HasIndex(x => x.Status);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ClaimId);
        });

        b.Entity<ClaimLine>(e =>
        {
            e.ToTable("claim_line");
            e.HasKey(x => x.ClaimLineId);
            e.Property(x => x.FulfillmentType).HasConversion<string>().HasColumnName("fulfillment_type");
            e.Property(x => x.CodeSystem).HasConversion<string>().HasColumnName("code_system");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.SystemRecommendation).HasConversion<string>().HasColumnName("system_recommendation");
            // text[] maps to a Postgres array column.
            e.Property(x => x.ReasonCodes).HasColumnName("reason_codes").HasColumnType("text[]");
            // xmin optimistic-concurrency guard: line decisions (10b.4) land only if the line hasn't moved.
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.ClaimId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.CodeSystem, x.Code });
        });

        b.Entity<ClaimBatch>(e =>
        {
            e.ToTable("claim_batch");
            e.HasKey(x => x.BatchId);
            e.Property(x => x.BatchType).HasConversion<string>().HasColumnName("batch_type");
            e.Property(x => x.SelectionMode).HasConversion<string>().HasColumnName("selection_mode");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.BatchNo).IsUnique();
            e.HasIndex(x => new { x.PayeeProviderId, x.PeriodFrom });
            e.HasIndex(x => x.Status);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.BatchId);
        });

        b.Entity<ClaimBatchItem>(e =>
        {
            e.ToTable("claim_batch_item");
            e.HasKey(x => x.BatchItemId);
            e.Property(x => x.BatchStatusSnapshot).HasConversion<string>().HasColumnName("batch_status");
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => x.ClaimId);
        });

        b.Entity<ClaimDecision>(e =>
        {
            e.ToTable("claim_decision");
            e.HasKey(x => x.DecisionId);
            e.Property(x => x.Decision).HasConversion<string>().HasColumnName("decision");
            e.Property(x => x.ReasonCodes).HasColumnName("reason_codes").HasColumnType("text[]");
            e.HasIndex(x => new { x.ClaimLineId, x.DecidedAt });
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
        });

        b.Entity<ClaimAdjustment>(e =>
        {
            e.ToTable("claim_adjustment");
            e.HasKey(x => x.AdjustmentId);
            e.Property(x => x.AdjustmentType).HasConversion<string>().HasColumnName("adjustment_type");
            e.HasIndex(x => new { x.ClaimLineId, x.AdjustedAt });
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
        });

        b.Entity<ClaimSubmission>(e =>
        {
            e.ToTable("claim_submission");
            e.HasKey(x => x.SubmissionId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.ProviderId, x.SubmittedAt });
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.SubmissionId);
        });

        b.Entity<ClaimSubmissionLine>(e =>
        {
            e.ToTable("claim_submission_line");
            e.HasKey(x => x.SubmissionLineId);
            e.Property(x => x.CodeSystem).HasConversion<string>().HasColumnName("code_system");
            e.Property(x => x.Outcome).HasConversion<string>().HasColumnName("outcome");
            e.HasIndex(x => x.SubmissionId);
        });

        b.Entity<ClaimDocument>(e =>
        {
            e.ToTable("claim_document");
            e.HasKey(x => x.ClaimDocumentId);
            e.Property(x => x.DocType).HasConversion<string>().HasColumnName("doc_type");
            e.HasIndex(x => x.DocumentId);
        });

        b.Entity<ReimbursementRequest>(e =>
        {
            e.ToTable("reimbursement_request");
            e.HasKey(x => x.RequestId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.MatchMethod).HasConversion<string>().HasColumnName("match_method");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            e.HasIndex(x => new { x.BeneficiaryId, x.SubmittedAt });
            e.HasIndex(x => x.Status);
        });

        b.Entity<OcrExtraction>(e =>
        {
            e.ToTable("ocr_extraction");
            e.HasKey(x => x.ExtractionId);
            e.Property(x => x.Region).HasColumnName("region").HasColumnType("jsonb");
            e.HasIndex(x => new { x.DocumentId, x.FieldName });
        });

        b.Entity<SettlementAdvice>(e =>
        {
            e.ToTable("settlement_advice");
            e.HasKey(x => x.AdviceId);
            e.HasIndex(x => new { x.BatchId, x.Version }).IsUnique();
        });

        b.Entity<SettlementPaymentReference>(e =>
        {
            e.ToTable("settlement_payment_reference");
            e.HasKey(x => x.PaymentReferenceId);
            e.HasIndex(x => x.BatchId);
        });

        b.Entity<ClaimAppeal>(e =>
        {
            e.ToTable("claim_appeal");
            e.HasKey(x => x.AppealId);
            e.Property(x => x.AppellantType).HasConversion<string>().HasColumnName("appellant_type");
            e.Property(x => x.Resolution).HasConversion<string>().HasColumnName("resolution");
            e.HasIndex(x => new { x.ClaimId, x.CreatedAt });
        });

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_event");
            e.HasKey(x => x.EventId);
        });
    }
}
