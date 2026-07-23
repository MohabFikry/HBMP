using Mersal.Provider.Domain;
using Microsoft.EntityFrameworkCore;
using ProviderEntity = Mersal.Provider.Domain.Provider;

namespace Mersal.Provider.Infrastructure;

public sealed class ProviderDbContext(DbContextOptions<ProviderDbContext> options) : DbContext(options)
{
    public const string Schema = "provider";

    public DbSet<ProviderEntity> Providers => Set<ProviderEntity>();
    public DbSet<ProviderLocation> Locations => Set<ProviderLocation>();
    public DbSet<ProviderContract> Contracts => Set<ProviderContract>();
    public DbSet<ContractServiceLine> ServiceLines => Set<ContractServiceLine>();
    public DbSet<ProviderCredential> Credentials => Set<ProviderCredential>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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
    }
}
