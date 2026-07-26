using Mersal.Admin.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Infrastructure;

/// <summary>EF Core context for the <c>admin</c> schema (phase 8b): role bindings, de-provision list, access-review
/// campaigns/items, session + device policy, and staged policy proposals. Every row (except the global policy
/// proposal) carries <c>tenant_id</c> and is RLS-isolated. Bindings are soft-lifecycle (revoke stamps metadata,
/// never delete).</summary>
public sealed class AdminDbContext(DbContextOptions<AdminDbContext> options) : DbContext(options)
{
    public const string Schema = "admin";

    public DbSet<RoleBinding> RoleBindings => Set<RoleBinding>();
    public DbSet<DeprovisionedUser> DeprovisionedUsers => Set<DeprovisionedUser>();
    public DbSet<AccessReviewCampaign> Campaigns => Set<AccessReviewCampaign>();
    public DbSet<AccessReviewItem> ReviewItems => Set<AccessReviewItem>();
    public DbSet<SessionPolicy> SessionPolicies => Set<SessionPolicy>();
    public DbSet<DevicePolicy> DevicePolicies => Set<DevicePolicy>();
    public DbSet<PolicyProposal> PolicyProposals => Set<PolicyProposal>();
    public DbSet<MasterDataVersion> MasterDataVersions => Set<MasterDataVersion>();
    public DbSet<NotificationTemplateVersion> TemplateVersions => Set<NotificationTemplateVersion>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();
    public DbSet<BreakGlassGrantRecord> BreakGlassGrants => Set<BreakGlassGrantRecord>();
    public DbSet<BreakGlassAccess> BreakGlassAccesses => Set<BreakGlassAccess>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<UserBranchAssignment> UserBranchAssignments => Set<UserBranchAssignment>();   // 14.2

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<RoleBinding>(e =>
        {
            e.ToTable("role_binding");
            e.HasKey(x => x.BindingId);
            e.Property(x => x.ScopeType).HasConversion<string>().HasColumnName("scope_type");
            e.Property(x => x.Tier).HasConversion<string>().HasColumnName("tier");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => new { x.TenantId, x.SubjectUserId });
            // one ACTIVE binding per (tenant, subject, role) — a revoked one may be re-granted later.
            e.HasIndex(x => new { x.TenantId, x.SubjectUserId, x.Role })
                .IsUnique()
                .HasFilter("status = 'Active'");
        });

        b.Entity<DeprovisionedUser>(e =>
        {
            e.ToTable("deprovisioned_user");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.SubjectUserId }).IsUnique();
        });

        b.Entity<AccessReviewCampaign>(e =>
        {
            e.ToTable("access_review_campaign");
            e.HasKey(x => x.CampaignId);
            e.Property(x => x.MinTier).HasConversion<string>().HasColumnName("min_tier");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.CampaignId);
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<AccessReviewItem>(e =>
        {
            e.ToTable("access_review_item");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.Decision).HasConversion<string>().HasColumnName("decision");
            e.HasIndex(x => x.CampaignId);
            e.HasIndex(x => x.BindingId);
        });

        b.Entity<SessionPolicy>(e =>
        {
            e.ToTable("session_policy");
            e.HasKey(x => x.PolicyId);
            e.Property(x => x.RoleTier).HasConversion<string>().HasColumnName("role_tier");
            e.HasIndex(x => new { x.TenantId, x.RoleTier });
        });

        b.Entity<DevicePolicy>(e =>
        {
            e.ToTable("device_policy");
            e.HasKey(x => x.PolicyId);
            e.Property(x => x.IpAllowListJson).HasColumnType("jsonb").HasColumnName("ip_allow_list");
            e.HasIndex(x => new { x.TenantId, x.Role });
        });

        b.Entity<PolicyProposal>(e =>
        {
            e.ToTable("policy_proposal");
            e.HasKey(x => x.ProposalId);
            e.Property(x => x.DiffJson).HasColumnType("jsonb").HasColumnName("diff");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
        });

        b.Entity<MasterDataVersion>(e =>
        {
            e.ToTable("master_data_version");
            e.HasKey(x => x.VersionId);
            e.Property(x => x.System).HasConversion<string>().HasColumnName("system");
            e.Property(x => x.AttributesJson).HasColumnType("jsonb").HasColumnName("attributes");
            e.HasIndex(x => new { x.System, x.Code });
            e.HasIndex(x => new { x.System, x.Code, x.VersionNo }).IsUnique();
        });

        b.Entity<NotificationTemplateVersion>(e =>
        {
            e.ToTable("notification_template_version");
            e.HasKey(x => x.TemplateVersionId);
            e.HasIndex(x => new { x.TenantId, x.TemplateKey, x.Channel });
            e.HasIndex(x => new { x.TenantId, x.TemplateKey, x.Channel, x.VersionNo }).IsUnique();
        });

        b.Entity<SystemConfig>(e =>
        {
            e.ToTable("system_config");
            e.HasKey(x => x.ConfigId);
            e.Property(x => x.ValueType).HasConversion<string>().HasColumnName("value_type");
            e.HasIndex(x => new { x.TenantId, x.Key });
            e.HasIndex(x => new { x.TenantId, x.Key, x.VersionNo }).IsUnique();
        });

        b.Entity<BreakGlassGrantRecord>(e =>
        {
            e.ToTable("break_glass_grant");
            e.HasKey(x => x.GrantId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.ScopedResourceTypesJson).HasColumnType("jsonb").HasColumnName("scoped_resource_types");
            e.Property(x => x.ScopedResourceIdsJson).HasColumnType("jsonb").HasColumnName("scoped_resource_ids");
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.RequesterUserId);
        });

        b.Entity<BreakGlassAccess>(e =>
        {
            e.ToTable("break_glass_access");
            e.HasKey(x => x.AccessId);
            e.HasIndex(x => x.GrantId);
        });

        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenant");
            e.HasKey(x => x.TenantId);
        });

        // 14.2 — staff↔branch assignments. Enums map to text; the one-active-Home invariant is a partial
        // unique index in the migration. Soft-lifecycle (revoke stamps metadata), tenant-scoped + RLS.
        b.Entity<UserBranchAssignment>(e =>
        {
            e.ToTable("user_branch_assignment");
            e.HasKey(x => x.AssignmentId);
            e.Property(x => x.AssignmentType).HasConversion<string>().HasColumnName("assignment_type");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => new { x.TenantId, x.SubjectUserId, x.Status });
            e.HasIndex(x => x.BranchId);
        });
    }
}
