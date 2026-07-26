using Mersal.Policy.Domain;
using PolicyEntity = Mersal.Policy.Domain.Policy;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Policy.Infrastructure;

public sealed class PolicyDbContext(DbContextOptions<PolicyDbContext> options) : DbContext(options)
{
    public const string Schema = "policy";

    public DbSet<PolicyEntity> Policies => Set<PolicyEntity>();
    public DbSet<BenefitCategory> BenefitCategories => Set<BenefitCategory>();
    public DbSet<Coverage> Coverages => Set<Coverage>();
    public DbSet<CoverageLimit> CoverageLimits => Set<CoverageLimit>();
    public DbSet<BenefitConsumptionRecord> BenefitConsumptions => Set<BenefitConsumptionRecord>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("policy");
        b.HasDefaultSchema(Schema);

        b.Entity<PolicyEntity>(e =>
        {
            e.ToTable("policy");
            e.HasKey(x => x.PolicyId);
            e.Property(x => x.PolicyNo).HasColumnName("policy_no").IsRequired();
            e.Property(x => x.Sponsor).HasColumnName("sponsor");
            e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            e.Property(x => x.EffectiveTo).HasColumnName("effective_to");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.PolicyNo).IsUnique();
        });

        b.Entity<BenefitCategory>(e =>
        {
            e.ToTable("benefit_category");
            e.HasKey(x => x.BenefitCategoryId);
            e.Property(x => x.Code).HasColumnName("code").IsRequired();
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<Coverage>(e =>
        {
            e.ToTable("coverage");
            e.HasKey(x => x.CoverageId);
            e.Property(x => x.PolicyId).HasColumnName("policy_id");
            e.Property(x => x.BeneficiaryId).HasColumnName("beneficiary_id");
            e.Property(x => x.BenefitCategoryId).HasColumnName("benefit_category_id");
            e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            e.Property(x => x.EffectiveTo).HasColumnName("effective_to");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.HasMany(x => x.Limits).WithOne().HasForeignKey(l => l.CoverageId);
            e.HasIndex(x => x.BeneficiaryId);
        });

        b.Entity<CoverageLimit>(e =>
        {
            e.ToTable("coverage_limit");
            e.HasKey(x => x.CoverageLimitId);
            e.Property(x => x.CoverageId).HasColumnName("coverage_id");
            e.Property(x => x.LimitType).HasConversion<string>().HasColumnName("limit_type");
            e.Property(x => x.LimitValue).HasColumnName("limit_value").HasColumnType("numeric(14,3)");
            e.Property(x => x.ConsumedValue).HasColumnName("consumed_value").HasColumnType("numeric(14,3)");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            e.Property(x => x.ResetPeriod).HasConversion<string>().HasColumnName("reset_period");
            e.Property(x => x.LastResetOn).HasColumnName("last_reset_on");
            e.Ignore(x => x.Remaining);
        });

        // 18.A1 — the accumulator's append-only ledger + the consumer's dedupe table (0003).
        b.Entity<BenefitConsumptionRecord>(e =>
        {
            e.ToTable("benefit_consumption");
            e.HasKey(x => x.ConsumptionId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
            e.Property(x => x.SourceRef).HasColumnName("source_ref").IsRequired();
            e.Property(x => x.BeneficiaryId).HasColumnName("beneficiary_id");
            e.Property(x => x.BenefitCategory).HasColumnName("benefit_category");
            e.Property(x => x.CoverageId).HasColumnName("coverage_id");
            e.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("numeric(14,3)");
            e.Property(x => x.Direction).HasConversion<string>().HasColumnName("direction");
            e.Property(x => x.Outcome).HasConversion<string>().HasColumnName("outcome");
            e.Property(x => x.MovedLimits).HasColumnName("moved_limits");
            e.Property(x => x.AppliedAt).HasColumnName("applied_at");
            e.HasIndex(x => x.SourceRef).IsUnique();
        });

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_event");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });
    }
}
