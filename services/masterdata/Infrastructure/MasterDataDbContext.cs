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
    public DbSet<DrugIndication> DrugIndications => Set<DrugIndication>();
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

        // Keyless: the shape of the 26.2 typeahead's raw SQL result, not a table.
        b.Entity<DrugSearchRow>(e => { e.HasNoKey(); e.ToView(null); });

        b.Entity<DrugIndication>(e =>
        {
            e.ToTable("drug_indication");
            e.HasKey(x => x.IndicationId);
            e.HasIndex(x => x.DrugId);
            e.HasIndex(x => x.IcdCode);
            e.HasIndex(x => new { x.DrugId, x.IcdCode }).IsUnique();
            e.Property(x => x.IcdCode).HasMaxLength(10);
            e.Property(x => x.Source).HasMaxLength(64);
            e.HasOne<Drug>().WithMany().HasForeignKey(x => x.DrugId).OnDelete(DeleteBehavior.Cascade);
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
            e.Property(x => x.AtcScopes).HasColumnName("atc_scopes");
            e.Property(x => x.IsDrugMappable).HasColumnName("is_drug_mappable");
            e.HasIndex(x => x.Code).IsUnique();
        });

        // ---- 28.1 the ingredient model (migration 0009) -------------------------------------------------
        b.Entity<Ingredient>(e =>
        {
            e.ToTable("ingredient");
            e.HasKey(x => x.IngredientId);
            e.HasIndex(x => x.IngredientKey).IsUnique();
        });

        b.Entity<DrugIngredient>(e =>
        {
            e.ToTable("drug_ingredient");
            e.HasKey(x => new { x.DrugId, x.IngredientKey });
            e.HasIndex(x => x.IngredientKey);
        });

        b.Entity<CrossReactivityGroup>(e =>
        {
            e.ToTable("cross_reactivity_group");
            e.HasKey(x => x.GroupCode);
            e.Property(x => x.Confidence).HasConversion<string>();
        });

        b.Entity<CrossReactivityMember>(e =>
        {
            e.ToTable("cross_reactivity_member");
            // No surrogate key in the table — the pair IS the row, and exactly one of the two targets is
            // non-null (CHECK ck_cross_reactivity_member_one_target). Keyless on the EF side because the
            // nullable half of the pair cannot participate in a key.
            e.HasNoKey();
        });

        b.Entity<AllergenIngredient>(e =>
        {
            e.ToTable("allergen_ingredient");
            e.HasKey(x => new { x.AllergenId, x.IngredientKey });
        });

        b.Entity<IcdAncestor>(e =>
        {
            e.ToTable("icd_ancestor");
            e.HasKey(x => new { x.Code, x.AncestorCode });
            e.HasIndex(x => x.Code);
            e.HasIndex(x => x.AncestorCode);
        });

        b.Entity<InteractionRule>(e =>
        {
            e.ToTable("interaction_rule");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.SubjectKind).HasConversion<string>();
            e.Property(x => x.ObjectKind).HasConversion<string>();
            e.Property(x => x.Severity).HasConversion<string>();
            e.Property(x => x.Onset).HasConversion<string>();
            e.Property(x => x.EvidenceLevel).HasConversion<string>();
            e.HasIndex(x => new { x.SubjectKind, x.SubjectValue });
            e.HasIndex(x => new { x.ObjectKind, x.ObjectValue });
        });

        b.Entity<DrugDiseaseContraindication>(e =>
        {
            e.ToTable("drug_disease_contraindication");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.SubjectKind).HasConversion<string>();
            e.Property(x => x.Severity).HasConversion<string>();
            e.Property(x => x.EvidenceLevel).HasConversion<string>();
            e.HasIndex(x => new { x.SubjectKind, x.SubjectValue });
            e.HasIndex(x => x.IcdScope);
        });

        b.Entity<DosingRule>(e =>
        {
            e.ToTable("dosing_rule");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.SubjectKind).HasConversion<string>();
            e.Property(x => x.Population).HasConversion<string>();
            e.HasIndex(x => new { x.SubjectKind, x.SubjectValue });
        });

        b.Entity<AllergenCrossReactivity>(e =>
        {
            e.ToTable("allergen_cross_reactivity");
            e.HasKey(x => new { x.AllergenId, x.GroupCode });
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
