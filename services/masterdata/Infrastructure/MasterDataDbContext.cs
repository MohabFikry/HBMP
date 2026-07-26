using Mersal.Events;
using Mersal.MasterData.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.MasterData.Infrastructure;

/// <summary>EF Core context for the read-mostly <c>masterdata</c> schema (22-data-dictionary §10.5).</summary>
public sealed class MasterDataDbContext(DbContextOptions<MasterDataDbContext> options) : DbContext(options)
{
    public const string Schema = "masterdata";

    public DbSet<IcdCode> IcdCodes => Set<IcdCode>();
    public DbSet<CptCode> CptCodes => Set<CptCode>();
    public DbSet<LoincCode> LoincCodes => Set<LoincCode>();
    public DbSet<AtcClass> AtcClasses => Set<AtcClass>();
    public DbSet<Drug> Drugs => Set<Drug>();
    public DbSet<DrugInteraction> DrugInteractions => Set<DrugInteraction>();
    public DbSet<Allergen> Allergens => Set<Allergen>();
    public DbSet<ExaminationType> ExaminationTypes => Set<ExaminationType>();   // 14.6

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<IcdCode>(e => { e.ToTable("icd_code"); e.HasKey(x => x.Code); e.HasIndex(x => x.Chapter); e.HasIndex(x => x.IsBillable);
            e.Property(x => x.Icd11Map).HasColumnName("icd11_map"); }); // snake convention renders digits oddly here
        b.Entity<CptCode>(e => { e.ToTable("cpt_code"); e.HasKey(x => x.Code); e.HasIndex(x => x.Category); });
        b.Entity<LoincCode>(e => { e.ToTable("loinc_code"); e.HasKey(x => x.Code); });
        b.Entity<AtcClass>(e => { e.ToTable("atc_class"); e.HasKey(x => x.AtcCode); e.HasIndex(x => x.Level); });

        b.Entity<Drug>(e =>
        {
            e.ToTable("drug");
            e.HasKey(x => x.DrugId);
            e.HasIndex(x => x.DrugCode).IsUnique();
            e.HasIndex(x => x.AtcCode);
            e.Property(x => x.PriceEgp).HasColumnType("numeric(14,2)");
            e.HasOne<AtcClass>().WithMany().HasForeignKey(x => x.AtcCode)
                .HasPrincipalKey(a => a.AtcCode).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<DrugInteraction>(e =>
        {
            e.ToTable("drug_interaction");
            e.HasKey(x => x.InteractionId);
            e.Property(x => x.Severity).HasConversion<string>();
            e.HasIndex(x => new { x.DrugAId, x.DrugBId }).IsUnique();
        });

        b.Entity<Allergen>(e =>
        {
            e.ToTable("allergen");
            e.HasKey(x => x.AllergenId);
            e.Property(x => x.Category).HasConversion<string>();
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<ExaminationType>(e =>
        {
            e.ToTable("examination_type");
            e.HasKey(x => x.ExaminationTypeId);
            e.Property(x => x.Category).HasConversion<string>();
            e.Property(x => x.SensitivityLevel).HasConversion<string>().HasColumnName("sensitivity_level");
            e.Property(x => x.SensitiveCategory).HasConversion<string>().HasColumnName("sensitive_category");
            e.Property(x => x.DefaultCodeSystem).HasColumnName("default_code_system");
            e.Property(x => x.DefaultCode).HasColumnName("default_code");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.SensitivityLevel);
        });

        b.AddOutbox(Schema); // 16.6 — durable outbox so screening-endpoint audit events are staged, not lost
    }
}
