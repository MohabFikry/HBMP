using Mersal.Events;
using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Infrastructure;

/// <summary>
/// The identity store (ADR-0015, Phase 17.1). ASP.NET Core Identity's EF store over the <c>identity</c>
/// Postgres schema, plus the roles/scopes-as-data model (<see cref="Scopes"/> + <see cref="RoleScopes"/>).
///
/// Table names are pinned explicitly (short, snake_case) so the hand-authored SQL migration
/// (<c>Migrations/0001_identity.sql</c>) and this model are one and the same — the repo applies SQL
/// migrations, not EF migrations, so the two must match exactly. Columns follow the snake_case naming
/// convention wired at registration.
/// </summary>
public sealed class IdentityStoreDbContext(DbContextOptions<IdentityStoreDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public const string Schema = "identity";

    public DbSet<Scope> Scopes => Set<Scope>();
    public DbSet<RoleScope> RoleScopes => Set<RoleScope>();

    /// <summary>21.1 — the security principal (design 40 §1). Authorization evaluates against these, not
    /// against <see cref="ApplicationUser"/>.</summary>
    public DbSet<TenantMembership> Memberships => Set<TenantMembership>();
    public DbSet<MembershipRole> MembershipRoles => Set<MembershipRole>();
    public DbSet<TenantMembershipHistory> MembershipHistory => Set<TenantMembershipHistory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        // OpenIddict's application/authorization/scope/token entities live in the same context + schema (17.2).
        builder.UseOpenIddict();

        // Durable transactional outbox for audit + domain events (admin actions audited, 17.4).
        builder.AddOutbox(Schema);

        // Short, stable table names (the SQL migration creates exactly these).
        builder.Entity<ApplicationUser>(e =>
        {
            e.ToTable("user");
            e.Property(u => u.TenantId).HasDefaultValue(string.Empty);
            e.Property(u => u.DisplayName).HasDefaultValue(string.Empty);
            e.Property(u => u.IsActive).HasDefaultValue(true);
        });
        builder.Entity<ApplicationRole>(e =>
        {
            e.ToTable("role");
            e.Property(r => r.SensitivityTier).HasMaxLength(2).HasDefaultValue("T1");
        });
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_role");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claim");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_login");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_token");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claim");

        builder.Entity<Scope>(e =>
        {
            e.ToTable("scope");
            e.HasKey(s => s.Name);
            e.Property(s => s.Name).HasMaxLength(64);
            e.Property(s => s.Domain).HasMaxLength(32);
        });

        builder.Entity<RoleScope>(e =>
        {
            e.ToTable("role_scope");
            e.HasKey(rs => new { rs.RoleName, rs.ScopeName });
            e.Property(rs => rs.RoleName).HasMaxLength(64);
            e.Property(rs => rs.ScopeName).HasMaxLength(64);
        });

        // ---- 21.1 membership model (Migrations/0010_tenant_membership.sql) ----
        builder.Entity<TenantMembership>(e =>
        {
            e.ToTable("tenant_membership");
            e.HasKey(m => m.MembershipId);
            e.Property(m => m.TenantId).IsRequired();
            // Stored as the CHECK-constrained string the migration declares, not as an int: a status that
            // reads 'Ended' in psql is one an operator can reason about during an incident.
            e.Property(m => m.Status).HasMaxLength(10).HasConversion<string>().HasDefaultValue(MembershipStatus.Invited);
            e.Property(m => m.IsDeleted).HasDefaultValue(false);
            e.Property(m => m.RowVersion).HasDefaultValue(0);
            e.HasOne<ApplicationUser>().WithMany(u => u.Memberships).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(m => m.Roles).WithOne().HasForeignKey(r => r.MembershipId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.UserId, m.TenantId }).IsUnique().HasFilter("NOT is_deleted");
        });

        builder.Entity<MembershipRole>(e =>
        {
            e.ToTable("membership_role");
            e.HasKey(r => new { r.MembershipId, r.RoleId });
            e.HasOne<ApplicationRole>().WithMany().HasForeignKey(r => r.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TenantMembershipHistory>(e =>
        {
            e.ToTable("tenant_membership_history");
            e.HasKey(h => h.HistoryId);
            e.Property(h => h.Status).HasMaxLength(10);
            e.Property(h => h.TenantId).IsRequired();
        });
    }
}
