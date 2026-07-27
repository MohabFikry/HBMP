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
    // 19.1 — the PAS product layer (design 38 §3).
    public DbSet<Payer> Payers => Set<Payer>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanVersion> PlanVersions => Set<PlanVersion>();
    public DbSet<BenefitRule> BenefitRules => Set<BenefitRule>();
    public DbSet<BenefitRuleTier> BenefitRuleTiers => Set<BenefitRuleTier>();   // 19.1b

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

        // ---- 19.1 PAS product layer -------------------------------------------------------------------
        b.Entity<Payer>(e =>
        {
            e.ToTable("payer");
            e.HasKey(x => x.PayerId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PayerCode).HasColumnName("payer_code").IsRequired();
            e.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
            e.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
            e.Property(x => x.PayerType).HasConversion<string>().HasColumnName("payer_type");
            e.Property(x => x.Contact).HasColumnName("contact").HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        });

        b.Entity<Plan>(e =>
        {
            e.ToTable("plan");
            e.HasKey(x => x.PlanId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PlanCode).HasColumnName("plan_code").IsRequired();
            e.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
            e.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Category).HasColumnName("category").IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        });

        b.Entity<PlanVersion>(e =>
        {
            e.ToTable("plan_version");
            e.HasKey(x => x.PlanVersionId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PlanId).HasColumnName("plan_id");
            e.Property(x => x.VersionNo).HasColumnName("version_no");
            e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            e.Property(x => x.EffectiveTo).HasColumnName("effective_to");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.ActivatedBy).HasColumnName("activated_by");
            e.Property(x => x.ActivatedAt).HasColumnName("activated_at");
            e.Property(x => x.SupersededByVersionId).HasColumnName("superseded_by_version_id");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasMany(x => x.Rules).WithOne().HasForeignKey(r => r.PlanVersionId);
            e.HasIndex(x => new { x.PlanId, x.VersionNo }).IsUnique();
            e.Ignore(x => x.IsEditable);
        });

        b.Entity<BenefitRule>(e =>
        {
            e.ToTable("benefit_rule");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PlanVersionId).HasColumnName("plan_version_id");
            e.Property(x => x.BenefitCategoryId).HasColumnName("benefit_category_id");
            e.Property(x => x.IsCovered).HasColumnName("is_covered");
            e.Property(x => x.LimitType).HasConversion<string>().HasColumnName("limit_type");
            e.Property(x => x.LimitValue).HasColumnName("limit_value").HasColumnType("numeric(14,2)");
            e.Property(x => x.ResetPeriod).HasConversion<string>().HasColumnName("reset_period");
            e.Property(x => x.Deductible).HasColumnName("deductible").HasColumnType("numeric(14,2)");
            e.Property(x => x.WaitingPeriodDays).HasColumnName("waiting_period_days");
            e.Property(x => x.RequiresPreauth).HasColumnName("requires_preauth");
            e.Property(x => x.PreauthCostThreshold).HasColumnName("preauth_cost_threshold").HasColumnType("numeric(14,2)");
            e.Property(x => x.Exclusions).HasColumnName("exclusions").HasColumnType("jsonb");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(x => new { x.PlanVersionId, x.BenefitCategoryId }).IsUnique();
            e.HasMany(x => x.Tiers).WithOne().HasForeignKey(t => t.BenefitRuleId);
        });

        // 19.1b — the per-tier cost-share grid (design 38 §3). network_tier_id is a cross-service VALUE.
        b.Entity<BenefitRuleTier>(e =>
        {
            e.ToTable("benefit_rule_tier");
            e.HasKey(x => x.RuleTierId);
            e.Property(x => x.RuleTierId).HasColumnName("rule_tier_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BenefitRuleId).HasColumnName("benefit_rule_id");
            e.Property(x => x.NetworkTierId).HasColumnName("network_tier_id");
            e.Property(x => x.TierCode).HasColumnName("tier_code").IsRequired();
            e.Property(x => x.IsCovered).HasColumnName("is_covered");
            e.Property(x => x.CopayFixed).HasColumnName("copay_fixed").HasColumnType("numeric(14,2)");
            e.Property(x => x.CopayPercent).HasColumnName("copay_percent").HasColumnType("numeric(5,2)");
            e.Property(x => x.CoinsurancePercent).HasColumnName("coinsurance_percent").HasColumnType("numeric(5,2)");
            e.Property(x => x.RequiresPreauthOverride).HasColumnName("requires_preauth_override");
            e.Property(x => x.LimitMultiplier).HasColumnName("limit_multiplier").HasColumnType("numeric(5,2)");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(x => new { x.BenefitRuleId, x.NetworkTierId }).IsUnique();
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
