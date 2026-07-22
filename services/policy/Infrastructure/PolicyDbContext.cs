using Mersal.Policy.Domain;
using PolicyEntity = Mersal.Policy.Domain.Policy;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

public sealed class PolicyDbContext(DbContextOptions<PolicyDbContext> options) : DbContext(options)
{
    public const string Schema = "policy";

    public DbSet<PolicyEntity> Policies => Set<PolicyEntity>();
    public DbSet<BenefitCategory> BenefitCategories => Set<BenefitCategory>();
    public DbSet<Coverage> Coverages => Set<Coverage>();
    public DbSet<CoverageLimit> CoverageLimits => Set<CoverageLimit>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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
    }
}
