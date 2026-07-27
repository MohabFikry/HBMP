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
    // 19.2 + 19.2b — the membership layer (design 38 §3–§4.2).
    public DbSet<PolicyPlan> PolicyPlans => Set<PolicyPlan>();
    public DbSet<MemberGroup> MemberGroups => Set<MemberGroup>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<EnrollmentEvent> EnrollmentEvents => Set<EnrollmentEvent>();
    public DbSet<Note> Notes => Set<Note>();   // 19.3
    public DbSet<PolicyDocument> PolicyDocuments => Set<PolicyDocument>();   // 19.3b
    public DbSet<TimelineEntry> TimelineEntries => Set<TimelineEntry>();     // 19.3c

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
            e.Property(x => x.PayerId).HasColumnName("payer_id");                       // 19.2
            e.Property(x => x.PreviousPolicyId).HasColumnName("previous_policy_id");     // 19.2
            e.Property(x => x.MaxMembers).HasColumnName("max_members");                  // 19.2
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
            // 19.2 — provenance: which plan version and which enrolment produced this entitlement.
            e.Property(x => x.SourcePlanVersionId).HasColumnName("source_plan_version_id");
            e.Property(x => x.EnrollmentId).HasColumnName("enrollment_id");
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
            e.Property(x => x.ProviderId).HasColumnName("provider_id");                       // 19.4
            e.Property(x => x.ProviderLocationId).HasColumnName("provider_location_id");      // 19.4
            e.Property(x => x.ServiceDate).HasColumnName("service_date");                     // 19.4
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
            e.Property(x => x.DeductibleWaived).HasColumnName("deductible_waived");
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
            e.Property(x => x.CopayCountsTowardDeductible).HasColumnName("copay_counts_toward_deductible");
            e.Property(x => x.RequiresPreauthOverride).HasColumnName("requires_preauth_override");
            e.Property(x => x.LimitMultiplier).HasColumnName("limit_multiplier").HasColumnType("numeric(5,2)");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(x => new { x.BenefitRuleId, x.NetworkTierId }).IsUnique();
        });

        // ---- 19.2 + 19.2b membership layer ------------------------------------------------------------
        b.Entity<PolicyPlan>(e =>
        {
            e.ToTable("policy_plan");
            e.HasKey(x => x.PolicyPlanId);
            e.Property(x => x.PolicyPlanId).HasColumnName("policy_plan_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PolicyId).HasColumnName("policy_id");
            e.Property(x => x.PlanVersionId).HasColumnName("plan_version_id");
            e.Property(x => x.PlanLabel).HasColumnName("plan_label").IsRequired();
            e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            e.Property(x => x.EffectiveTo).HasColumnName("effective_to");
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.EligibilityRule).HasColumnName("eligibility_rule").HasColumnType("jsonb");
            e.Property(x => x.MaxMembers).HasColumnName("max_members");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(x => x.PolicyId);
        });

        b.Entity<MemberGroup>(e =>
        {
            e.ToTable("member_group");
            e.HasKey(x => x.GroupId);
            e.Property(x => x.GroupId).HasColumnName("group_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PolicyId).HasColumnName("policy_id");
            e.Property(x => x.GroupCode).HasColumnName("group_code").IsRequired();
            e.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
            e.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
            e.Property(x => x.GroupType).HasConversion<string>().HasColumnName("group_type");
            e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            e.Property(x => x.EffectiveTo).HasColumnName("effective_to");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(x => x.PolicyId);
        });

        b.Entity<Enrollment>(e =>
        {
            e.ToTable("enrollment");
            e.HasKey(x => x.EnrollmentId);
            e.Property(x => x.EnrollmentId).HasColumnName("enrollment_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BeneficiaryId).HasColumnName("beneficiary_id");
            e.Property(x => x.PolicyId).HasColumnName("policy_id");
            e.Property(x => x.PolicyPlanId).HasColumnName("policy_plan_id");
            e.Property(x => x.GroupId).HasColumnName("group_id");
            e.Property(x => x.MemberNo).HasColumnName("member_no").IsRequired();
            e.Property(x => x.Relationship).HasConversion<string>().HasColumnName("relationship");
            e.Property(x => x.PrincipalEnrollmentId).HasColumnName("principal_enrollment_id");
            e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            e.Property(x => x.EffectiveTo).HasColumnName("effective_to");
            e.Property(x => x.WaitingPeriodEndsOn).HasColumnName("waiting_period_ends_on");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.TerminationReason).HasColumnName("termination_reason");
            e.Property(x => x.SourcePlanVersionId).HasColumnName("source_plan_version_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");   // 19.5 — the ENROLLING branch
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(x => x.BeneficiaryId);
            e.HasIndex(x => new { x.PolicyId, x.Status });
        });

        b.Entity<EnrollmentEvent>(e =>
        {
            e.ToTable("enrollment_event");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EnrollmentId).HasColumnName("enrollment_id");
            e.Property(x => x.EventType).HasConversion<string>().HasColumnName("event_type");
            e.Property(x => x.EffectiveDate).HasColumnName("effective_date");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Ignore(x => x.IsRetroEffective);
            e.HasIndex(x => new { x.EnrollmentId, x.OccurredAt });
        });

        // 19.3 — notes on policy and member (design 38 §5). Append-only + signed; see 0009.
        b.Entity<Note>(e =>
        {
            e.ToTable("note");
            e.HasKey(x => x.NoteId);
            e.Property(x => x.NoteId).HasColumnName("note_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Scope).HasConversion<string>().HasColumnName("scope");
            e.Property(x => x.ScopeRef).HasColumnName("scope_ref");
            e.Property(x => x.NoteType).HasConversion<string>().HasColumnName("note_type");
            e.Property(x => x.Body).HasColumnName("body").IsRequired();
            e.Property(x => x.VisibilityClass).HasConversion<string>().HasColumnName("visibility_class");
            e.Property(x => x.AuthoredByUserId).HasColumnName("authored_by_user_id");
            e.Property(x => x.AuthoredByUsername).HasColumnName("authored_by_username").IsRequired();
            e.Property(x => x.AuthoredByDisplay).HasColumnName("authored_by_display").IsRequired();
            e.Property(x => x.AuthoredAt).HasColumnName("authored_at");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.CancelledByUserId).HasColumnName("cancelled_by_user_id");
            e.Property(x => x.CancelledByUsername).HasColumnName("cancelled_by_username");
            e.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
            e.Property(x => x.CancellationReason).HasColumnName("cancellation_reason");
            e.Property(x => x.SupersedesNoteId).HasColumnName("supersedes_note_id");
            e.Property(x => x.Pinned).HasColumnName("pinned");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Ignore(x => x.ReadIsAuditable);
            e.HasIndex(x => new { x.Scope, x.ScopeRef, x.AuthoredAt });
        });

        // 19.3b — documents on policy and member (design 38 §5b). Bytes live in document-service/MinIO.
        b.Entity<PolicyDocument>(e =>
        {
            e.ToTable("policy_document");
            e.HasKey(x => x.LinkId);
            e.Property(x => x.LinkId).HasColumnName("link_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Scope).HasConversion<string>().HasColumnName("scope");
            e.Property(x => x.ScopeRef).HasColumnName("scope_ref");
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.VersionNo).HasColumnName("version_no");
            e.Property(x => x.SupersedesLinkId).HasColumnName("supersedes_link_id");
            e.Property(x => x.DocumentClass).HasConversion<string>().HasColumnName("document_class");
            e.Property(x => x.VisibilityClass).HasConversion<string>().HasColumnName("visibility_class");
            e.Property(x => x.SensitiveCategory).HasConversion<string>().HasColumnName("sensitive_category");
            e.Property(x => x.Title).HasColumnName("title").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.DocumentDate).HasColumnName("document_date");
            e.Property(x => x.IssuingProvider).HasColumnName("issuing_provider");
            e.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by_user_id");
            e.Property(x => x.UploadedByUsername).HasColumnName("uploaded_by_username").IsRequired();
            e.Property(x => x.UploadedByDisplay).HasColumnName("uploaded_by_display").IsRequired();
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.WithdrawnByUserId).HasColumnName("withdrawn_by_user_id");
            e.Property(x => x.WithdrawnByUsername).HasColumnName("withdrawn_by_username");
            e.Property(x => x.WithdrawnAt).HasColumnName("withdrawn_at");
            e.Property(x => x.WithdrawalReason).HasColumnName("withdrawal_reason");
            e.Property(x => x.ExpiresOn).HasColumnName("expires_on");
            e.Property(x => x.VerifiedByUserId).HasColumnName("verified_by_user_id");
            e.Property(x => x.VerifiedByUsername).HasColumnName("verified_by_username");
            e.Property(x => x.VerifiedAt).HasColumnName("verified_at");
            e.Property(x => x.VerificationNote).HasColumnName("verification_note");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Ignore(x => x.IsPhi);
            e.HasIndex(x => new { x.Scope, x.ScopeRef, x.UploadedAt });
        });

        // 19.3c — the change timeline: a PROJECTION over the audit stream, never a second log.
        b.Entity<TimelineEntry>(e =>
        {
            e.ToTable("entity_timeline");
            e.HasKey(x => x.EntryId);
            e.Property(x => x.EntryId).HasColumnName("entry_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Scope).HasConversion<string>().HasColumnName("scope");
            e.Property(x => x.ScopeRef).HasColumnName("scope_ref");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
            e.Property(x => x.EventCategory).HasConversion<string>().HasColumnName("event_category");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.ActorUsername).HasColumnName("actor_username");
            e.Property(x => x.ActorDisplay).HasColumnName("actor_display");
            e.Property(x => x.SummaryEn).HasColumnName("summary_en").IsRequired();
            e.Property(x => x.SummaryAr).HasColumnName("summary_ar").IsRequired();
            e.Property(x => x.ChangeDiff).HasColumnName("change_diff").HasColumnType("jsonb");
            e.Property(x => x.VisibilityClass).HasConversion<string>().HasColumnName("visibility_class");
            e.Property(x => x.SourceService).HasColumnName("source_service").IsRequired();
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.SourceEventId).HasColumnName("source_event_id");
            e.Property(x => x.TargetRef).HasColumnName("target_ref");
            e.Property(x => x.TargetKind).HasColumnName("target_kind");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.SourceEventId).IsUnique();
            e.HasIndex(x => new { x.Scope, x.ScopeRef, x.OccurredAt });
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
