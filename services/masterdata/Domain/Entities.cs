namespace Mersal.MasterData.Domain;

// Master-data entities per 22-data-dictionary.md §10.5 and 15-database-erd.md §13.
// Reference tables (icd/cpt/loinc/atc) key by natural code; drug/interaction/allergen use uuid v7.
// A source_release/version column set makes loads versioned + trackable + reversible.

/// <summary>ICD-10 diagnosis code. PK = code (dotted format, e.g. "E11.9").</summary>
public sealed class IcdCode
{
    public string Code { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string? Chapter { get; set; }
    public bool IsBillable { get; set; }
    public string? Icd11Map { get; set; }        // ICD-11 ready (nullable)
    public string? SourceRelease { get; set; }
}

/// <summary>CPT procedure code. PK = code.</summary>
public sealed class CptCode
{
    public string Code { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string? Category { get; set; }
    public string? SourceRelease { get; set; }
}

/// <summary>LOINC lab observation code. PK = code.</summary>
public sealed class LoincCode
{
    public string Code { get; set; } = default!;
    public string LongName { get; set; } = default!;
    public string? Component { get; set; }
    public string? Property { get; set; }
    public string? SourceRelease { get; set; }
}

/// <summary>ATC classification node. PK = atc_code; level 1..5 (Anatomical→Chemical Substance).</summary>
public sealed class AtcClass
{
    public string AtcCode { get; set; } = default!;
    public string Title { get; set; } = default!;
    public int Level { get; set; }
    public string? SourceRelease { get; set; }
}

/// <summary>A marketed drug (Egyptian drug master). Surrogate uuid v7 PK; drug_code UK.</summary>
public sealed class Drug
{
    public Guid DrugId { get; set; }
    public string DrugCode { get; set; } = default!;   // stable natural key (normalized commercial name)
    public string Name { get; set; } = default!;
    public string? NameAr { get; set; }
    public string? ScientificName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Form { get; set; }                   // route/form
    public string? Strength { get; set; }
    public string? AtcCode { get; set; }                // FK → atc_class (nullable if unmatched)
    public decimal? PriceEgp { get; set; }
    public string? SourceRelease { get; set; }
}

public enum InteractionSeverity { Minor, Moderate, Major, Contraindicated }

/// <summary>A drug-drug interaction (order-insensitive pair).</summary>
public sealed class DrugInteraction
{
    public Guid InteractionId { get; set; }
    public Guid DrugAId { get; set; }
    public Guid DrugBId { get; set; }
    public InteractionSeverity Severity { get; set; }
    public string? Description { get; set; }
    public string? SourceRelease { get; set; }
}

public enum AllergenCategory { Drug, Food, Environmental }

/// <summary>An allergen catalog entry.</summary>
public sealed class Allergen
{
    public Guid AllergenId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public AllergenCategory Category { get; set; }
    public string? SourceRelease { get; set; }
}
