using Mersal.Provider.Domain;
using Microsoft.EntityFrameworkCore;
using ProviderEntity = Mersal.Provider.Domain.Provider;
using Mersal.Events;

namespace Mersal.Provider.Infrastructure;

public sealed class ProviderDbContext(DbContextOptions<ProviderDbContext> options) : DbContext(options)
{
    public const string Schema = "provider";

    public DbSet<ProviderEntity> Providers => Set<ProviderEntity>();
    public DbSet<ProviderLocation> Locations => Set<ProviderLocation>();
    public DbSet<ProviderContract> Contracts => Set<ProviderContract>();
    public DbSet<ContractServiceLine> ServiceLines => Set<ContractServiceLine>();
    public DbSet<ProviderCredential> Credentials => Set<ProviderCredential>();
    public DbSet<ProviderUser> Users => Set<ProviderUser>();
    public DbSet<Branch> Branches => Set<Branch>();   // 14.1 — internal Mersal facilities (not provider_location)
    public DbSet<Specialty> Specialties => Set<Specialty>();                       // 14.5
    public DbSet<Practitioner> Practitioners => Set<Practitioner>();               // 14.5
    public DbSet<PractitionerSpecialty> PractitionerSpecialties => Set<PractitionerSpecialty>();          // 14.5
    public DbSet<PractitionerBranchAssignment> PractitionerBranchAssignments => Set<PractitionerBranchAssignment>();   // 14.5
    public DbSet<NetworkTier> NetworkTiers => Set<NetworkTier>();                                   // 19.1b
    public DbSet<ProviderNetworkAssignment> NetworkAssignments => Set<ProviderNetworkAssignment>(); // 19.1b

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("provider");
        b.HasDefaultSchema(Schema);

        b.Entity<ProviderEntity>(e =>
        {
            e.ToTable("provider");
            e.HasKey(x => x.ProviderId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.ProviderCode).HasColumnName("provider_code").IsRequired();
            e.Property(x => x.LegalName).HasColumnName("legal_name").IsRequired();
            e.Property(x => x.ProviderType).HasConversion<string>().HasColumnName("provider_type");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.OnboardingState).HasConversion<string>().HasColumnName("onboarding_state");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.TenantId, x.ProviderCode }).IsUnique();
            e.HasMany(x => x.Locations).WithOne().HasForeignKey(l => l.ProviderId);
            e.HasMany(x => x.Contracts).WithOne().HasForeignKey(c => c.ProviderId);
            e.HasMany(x => x.Credentials).WithOne().HasForeignKey(c => c.ProviderId);
        });

        b.Entity<ProviderLocation>(e =>
        {
            e.ToTable("provider_location");
            e.HasKey(x => x.LocationId);
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Governorate).HasColumnName("governorate");
            e.Property(x => x.Address).HasColumnName("address");
            e.Property(x => x.GeoLat).HasColumnName("geo_lat").HasColumnType("numeric(9,6)");
            e.Property(x => x.GeoLng).HasColumnName("geo_lng").HasColumnType("numeric(9,6)");
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        });

        b.Entity<ProviderContract>(e =>
        {
            e.ToTable("provider_contract");
            e.HasKey(x => x.ContractId);
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.ContractNo).HasColumnName("contract_no").IsRequired();
            e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            e.Property(x => x.EffectiveTo).HasColumnName("effective_to");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.HasIndex(x => new { x.TenantId, x.ContractNo }).IsUnique();
            e.HasMany(x => x.ServiceLines).WithOne().HasForeignKey(l => l.ContractId);
        });

        b.Entity<ContractServiceLine>(e =>
        {
            e.ToTable("contract_service_line");
            e.HasKey(x => x.ServiceLineId);
            e.Property(x => x.ContractId).HasColumnName("contract_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.ServiceType).HasConversion<string>().HasColumnName("service_type");
            e.Property(x => x.CodeSystem).HasConversion<string>().HasColumnName("code_system");
            e.Property(x => x.Code).HasColumnName("code").IsRequired();
            e.Property(x => x.AgreedPrice).HasColumnName("agreed_price").HasColumnType("numeric(14,2)");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            e.HasIndex(x => new { x.ContractId, x.CodeSystem, x.Code }).IsUnique();
        });

        b.Entity<ProviderCredential>(e =>
        {
            e.ToTable("provider_credential");
            e.HasKey(x => x.CredentialId);
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.CredentialType).HasColumnName("credential_type").IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.ValidFrom).HasColumnName("valid_from");
            e.Property(x => x.ValidTo).HasColumnName("valid_to");
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.IsMandatory).HasColumnName("is_mandatory");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        });

        b.Entity<ProviderUser>(e =>
        {
            e.ToTable("provider_user");
            e.HasKey(x => x.UserId);
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.SubjectRef).HasColumnName("subject_ref").IsRequired();
            e.Property(x => x.Role).HasColumnName("role").IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(x => x.ProviderId);
            e.HasIndex(x => new { x.TenantId, x.SubjectRef }).IsUnique();
        });

        // 14.1 — internal Mersal branch (org reference data, NOT tenant/provider scoped: the six branches are
        // shared facilities, so no RLS predicate applies here — unlike the provider tables above).
        b.Entity<Branch>(e =>
        {
            e.ToTable("branch");
            e.HasKey(x => x.BranchId);
            e.Property(x => x.BranchCode).HasColumnName("branch_code").IsRequired();
            e.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
            e.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
            e.Property(x => x.City).HasColumnName("city");
            e.Property(x => x.Address).HasColumnName("address");
            e.Property(x => x.Timezone).HasColumnName("timezone").IsRequired();
            e.Property(x => x.Phone).HasColumnName("phone");
            e.Property(x => x.OpeningHours).HasColumnName("opening_hours").HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.BranchCode).IsUnique().HasFilter("is_deleted = false");
            e.HasIndex(x => x.Status);
        });

        // 14.5 — practitioners, specialty & branch assignment (design 37 §4).
        b.Entity<Specialty>(e =>
        {
            e.ToTable("specialty");
            e.HasKey(x => x.SpecialtyCode);
            e.Property(x => x.SpecialtyCode).HasColumnName("specialty_code");
            e.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
            e.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
            e.Property(x => x.ParentCode).HasColumnName("parent_code");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        });

        b.Entity<Practitioner>(e =>
        {
            e.ToTable("practitioner");
            e.HasKey(x => x.PractitionerId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.PractitionerType).HasConversion<string>().HasColumnName("practitioner_type");
            e.Property(x => x.FullNameEn).HasColumnName("full_name_en").IsRequired();
            e.Property(x => x.FullNameAr).HasColumnName("full_name_ar").IsRequired();
            e.Property(x => x.LicenseNo).HasColumnName("license_no");
            e.Property(x => x.LicenseExpiry).HasColumnName("license_expiry");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.UserId).IsUnique().HasFilter("is_deleted = false");
            e.HasMany(x => x.Specialties).WithOne().HasForeignKey(s => s.PractitionerId);
            e.HasMany(x => x.BranchAssignments).WithOne().HasForeignKey(a => a.PractitionerId);
        });

        b.Entity<PractitionerSpecialty>(e =>
        {
            e.ToTable("practitioner_specialty");
            e.HasKey(x => new { x.PractitionerId, x.SpecialtyCode });
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
        });

        b.Entity<PractitionerBranchAssignment>(e =>
        {
            e.ToTable("practitioner_branch_assignment");
            e.HasKey(x => x.AssignmentId);
            e.Property(x => x.PractitionerId).HasColumnName("practitioner_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.ValidFrom).HasColumnName("valid_from");
            e.Property(x => x.ValidTo).HasColumnName("valid_to");
            e.Property(x => x.Status).HasColumnName("status");
            e.HasIndex(x => new { x.PractitionerId, x.Status });
            e.HasIndex(x => x.BranchId);
        });

        // 19.1b — network tiers + effective-dated tier assignment (design 38 §3, §4.1b).
        b.Entity<NetworkTier>(e =>
        {
            e.ToTable("network_tier");
            e.HasKey(x => x.NetworkTierId);
            e.Property(x => x.NetworkTierId).HasColumnName("network_tier_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.TierCode).HasColumnName("tier_code").IsRequired();
            e.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
            e.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
            e.Property(x => x.Rank).HasColumnName("rank");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.IsOutOfNetwork).HasColumnName("is_out_of_network");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(x => new { x.TenantId, x.TierCode }).IsUnique().HasFilter("NOT is_deleted");
        });

        b.Entity<ProviderNetworkAssignment>(e =>
        {
            e.ToTable("provider_network_assignment");
            e.HasKey(x => x.AssignmentId);
            e.Property(x => x.AssignmentId).HasColumnName("assignment_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.NetworkTierId).HasColumnName("network_tier_id");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.Scope).HasConversion<string>().HasColumnName("scope");
            e.Property(x => x.ScopeRef).HasColumnName("scope_ref");
            e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            e.Property(x => x.EffectiveTo).HasColumnName("effective_to");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.RevokedReason).HasColumnName("revoked_reason");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(x => new { x.Scope, x.ScopeRef, x.EffectiveFrom });
            e.HasIndex(x => x.ProviderId);
        });
    }
}
