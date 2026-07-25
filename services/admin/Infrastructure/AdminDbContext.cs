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
    }
}
