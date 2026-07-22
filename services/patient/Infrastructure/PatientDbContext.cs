using Mersal.Patient.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Patient.Infrastructure;

/// <summary>EF Core context for the <c>patient</c> schema (schema-per-service + RLS).</summary>
public sealed class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options)
{
    public const string Schema = "patient";

    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
    public DbSet<BeneficiaryIdentifier> Identifiers => Set<BeneficiaryIdentifier>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<FamilyGroup> FamilyGroups => Set<FamilyGroup>();
    public DbSet<DependentLink> DependentLinks => Set<DependentLink>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Beneficiary>(e =>
        {
            e.ToTable("beneficiary");
            e.HasKey(x => x.BeneficiaryId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.MemberNo).HasColumnName("member_no");
            e.Property(x => x.GivenName).HasColumnName("given_name").IsRequired();
            e.Property(x => x.FamilyName).HasColumnName("family_name").IsRequired();
            e.Property(x => x.BirthDate).HasColumnName("birth_date");
            e.Property(x => x.Sex).HasColumnName("sex");
            e.Property(x => x.NationalityCode).HasColumnName("nationality_code");
            e.Property(x => x.FamilyGroupId).HasColumnName("family_group_id");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.MemberNo).IsUnique();
            e.HasMany(x => x.Identifiers).WithOne().HasForeignKey(i => i.BeneficiaryId);
            e.HasMany(x => x.Contacts).WithOne().HasForeignKey(c => c.BeneficiaryId);
        });

        b.Entity<BeneficiaryIdentifier>(e =>
        {
            e.ToTable("beneficiary_identifier");
            e.HasKey(x => x.IdentifierId);
            e.Property(x => x.BeneficiaryId).HasColumnName("beneficiary_id");
            e.Property(x => x.IdentifierType).HasConversion<string>().HasColumnName("identifier_type");
            e.Property(x => x.IdentifierValue).HasColumnName("identifier_value").IsRequired();
            e.Property(x => x.IssuingCountry).HasColumnName("issuing_country");
            e.Property(x => x.ValidFrom).HasColumnName("valid_from");
            e.Property(x => x.ValidTo).HasColumnName("valid_to");
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            // The dedup partial unique index is created in SQL (WHERE is_deleted = false).
        });

        b.Entity<Contact>(e =>
        {
            e.ToTable("contact");
            e.HasKey(x => x.ContactId);
            e.Property(x => x.BeneficiaryId).HasColumnName("beneficiary_id");
            e.Property(x => x.ContactType).HasConversion<string>().HasColumnName("contact_type");
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.PreferredChannel).HasColumnName("preferred_channel");
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        });

        b.Entity<FamilyGroup>(e =>
        {
            e.ToTable("family_group");
            e.HasKey(x => x.FamilyGroupId);
            e.Property(x => x.FamilyCode).HasColumnName("family_code").IsRequired();
            e.Property(x => x.HeadBeneficiaryId).HasColumnName("head_beneficiary_id");
            e.HasIndex(x => x.FamilyCode).IsUnique();
        });

        b.Entity<DependentLink>(e =>
        {
            e.ToTable("dependent_link");
            e.HasKey(x => x.DependentLinkId);
            e.Property(x => x.FamilyGroupId).HasColumnName("family_group_id");
            e.Property(x => x.GuardianBeneficiaryId).HasColumnName("guardian_beneficiary_id");
            e.Property(x => x.DependentBeneficiaryId).HasColumnName("dependent_beneficiary_id");
            e.Property(x => x.Relationship).HasConversion<string>().HasColumnName("relationship");
        });
    }
}
