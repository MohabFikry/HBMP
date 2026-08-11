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

    /// <summary>21.5 — session/device controls and sign-in history (design 40 §6).</summary>
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    /// <summary>Staff avatars (28.15). Its own table so the hot `user` row stays narrow — see 0038.</summary>
    public DbSet<UserPhoto> UserPhotos => Set<UserPhoto>();

    /// <summary>21.2 — the per-membership override overlay (design 40 §2).</summary>
    public DbSet<MembershipOverride> Overrides => Set<MembershipOverride>();
    public DbSet<MembershipOverrideHistory> OverrideHistory => Set<MembershipOverrideHistory>();

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
            // Bounded because it is free text an administrator types and it renders in a fixed-width app-bar
            // slot. Nullable rather than defaulted to "": an account whose title nobody has recorded is a
            // different thing from one whose title is blank, and the app bar falls back for the first.
            e.Property(u => u.Position).HasMaxLength(120);
            e.Property(u => u.IsActive).HasDefaultValue(true);
        });
        builder.Entity<UserPhoto>(e =>
        {
            e.ToTable("user_photo");
            e.HasKey(x => x.UserId);
            e.Property(x => x.ContentType).HasMaxLength(40);
            e.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserPhoto>(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ApplicationRole>(e =>
        {
            e.ToTable("role");
            e.Property(r => r.SensitivityTier).HasMaxLength(2).HasDefaultValue("T1");
            // 28.9 — null for the built-in catalog, which is every role that predates custom roles (0036).
            e.Property(r => r.OwnerTenantId).HasMaxLength(64);
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
            // 21.2 catalog metadata (0013). Both flags default FALSE, so a key that predates this migration
            // is neither deprecated nor reachable by the platform-admin short-circuit.
            e.Property(s => s.Deprecated).HasDefaultValue(false);
            e.Property(s => s.ReplacedBy).HasMaxLength(64);
            e.Property(s => s.IsPlatformAdminKey).HasDefaultValue(false);
        });

        builder.Entity<RoleScope>(e =>
        {
            e.ToTable("role_scope");
            // 21.1b — grants are tenant-local (0011). TenantId leads the key so a tenant's whole grant set
            // is one index range; "" is the platform default bucket, not a real tenant.
            e.HasKey(rs => new { rs.TenantId, rs.RoleName, rs.ScopeName });
            e.Property(rs => rs.TenantId).HasDefaultValue(RoleScope.PlatformDefault);
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

        // ---- 21.2 per-membership overrides (Migrations/0013_catalog_and_overrides.sql) ----
        builder.Entity<MembershipOverride>(e =>
        {
            e.ToTable("membership_override");
            e.HasKey(o => o.OverrideId);
            e.Property(o => o.ScopeKey).HasMaxLength(64);
            // Stored as the CHECK-constrained string, for the same reason the membership status is: an
            // operator reading this table during an incident should see 'Deny', not a 1.
            e.Property(o => o.Effect).HasMaxLength(5).HasConversion<string>();
            e.Property(o => o.Reason).HasMaxLength(300).IsRequired();
            e.Property(o => o.IsDeleted).HasDefaultValue(false);
            e.Property(o => o.RowVersion).HasDefaultValue(0);
            e.HasOne<TenantMembership>().WithMany().HasForeignKey(o => o.MembershipId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(o => new { o.MembershipId, o.ScopeKey }).IsUnique().HasFilter("NOT is_deleted");
        });

        // ---- 21.5 sessions + login history (Migrations/0014_sessions_and_login_history.sql) ----
        builder.Entity<UserSession>(e =>
        {
            e.ToTable("user_session");
            e.HasKey(s => s.SessionId);
            e.Property(s => s.UserAgent).HasMaxLength(400);
            e.Property(s => s.RevokeReason).HasMaxLength(200);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.UserId, s.CreatedAt });
        });

        builder.Entity<LoginAttempt>(e =>
        {
            e.ToTable("login_attempt");
            e.HasKey(a => a.AttemptId);
            e.Property(a => a.UsernameTried).HasMaxLength(256).IsRequired();
            e.Property(a => a.FailureReason).HasMaxLength(40);
            e.Property(a => a.UserAgent).HasMaxLength(400);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<MembershipOverrideHistory>(e =>
        {
            e.ToTable("membership_override_history");
            e.HasKey(h => h.HistoryId);
            e.Property(h => h.ScopeKey).HasMaxLength(64);
            e.Property(h => h.Effect).HasMaxLength(5);
            e.Property(h => h.Reason).HasMaxLength(300);
        });
    }
}
