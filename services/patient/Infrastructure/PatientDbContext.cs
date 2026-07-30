using Mersal.Patient.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

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
    public DbSet<Registration> Registrations => Set<Registration>();
    public DbSet<EnrolmentIntent> EnrolmentIntents => Set<EnrolmentIntent>();
    public DbSet<RegistrationNote> RegistrationNotes => Set<RegistrationNote>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("patient");
        b.HasDefaultSchema(Schema);

        b.Entity<Registration>(e =>
        {
            e.ToTable("registration");
            e.HasKey(x => x.RegistrationId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });

        b.Entity<EnrolmentIntent>(e =>
        {
            e.ToTable("enrolment_intent");
            e.HasKey(x => x.RegistrationId);
            e.Property(x => x.RegistrationId).HasColumnName("registration_id");
            // The relationship is declared even though nothing navigates it, because EF orders its INSERT
            // batch by the dependencies it can SEE. Without this edge the intent and its registration are
            // independent roots, EF is free to write them in either order, and it picked the one that
            // violates the foreign key — a 500 on every registration, and only at runtime.
            e.HasOne<Registration>().WithOne()
                .HasForeignKey<EnrolmentIntent>(x => x.RegistrationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.PlanId).HasColumnName("plan_id");
            e.Property(x => x.NetworkTierId).HasColumnName("network_tier_id");
            e.Property(x => x.ContributionPercent).HasColumnName("contribution_percent").HasPrecision(5, 2);
            e.Property(x => x.DefaultBranchId).HasColumnName("default_branch_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<RegistrationNote>(e =>
        {
            e.ToTable("registration_note");
            // Composite key: one slot may be filled once per registration, which is the invariant rather
            // than a surrogate id that would let the same slot exist twice.
            e.HasKey(x => new { x.RegistrationId, x.Slot });
            e.Property(x => x.RegistrationId).HasColumnName("registration_id");
            // Same reason as the intent above: the edge is what makes EF write the registration first.
            e.HasOne<Registration>().WithMany()
                .HasForeignKey(x => x.RegistrationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Slot).HasColumnName("slot");
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.Visibility).HasConversion<string>().HasColumnName("visibility");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<Beneficiary>(e =>
        {
            e.ToTable("beneficiary");
            e.HasKey(x => x.BeneficiaryId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.MemberNo).HasColumnName("member_no");
            e.Property(x => x.CardNumber).HasColumnName("card_number");
            e.Property(x => x.GivenName).HasColumnName("given_name").IsRequired();
            e.Property(x => x.MiddleName).HasColumnName("middle_name");
            e.Property(x => x.FamilyName).HasColumnName("family_name").IsRequired();
            e.Property(x => x.BirthDate).HasColumnName("birth_date");
            e.Property(x => x.BirthDateIsApproximate).HasColumnName("birth_date_is_approximate");
            e.Property(x => x.Sex).HasColumnName("sex");
            e.Property(x => x.NationalityCode).HasColumnName("nationality_code");
            e.Property(x => x.IndividualNo).HasColumnName("individual_no");
            e.Property(x => x.CaseNo).HasColumnName("case_no");
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
