using CsvHelper.Configuration.Attributes;

namespace Mersal.MasterData.Loader;

// CSV record shapes bound to the REAL inspected headers (see loader README for the mapping).

/// <summary>Raw Files/ICD10_2019_full.csv</summary>
public sealed class IcdCsvRow
{
    [Name("Code")] public string Code { get; set; } = "";
    [Name("Description")] public string Description { get; set; } = "";
    [Name("Type")] public string Type { get; set; } = "";                 // chapter|block|category|subcategory
    [Name("Chapter_Description")] public string? ChapterDescription { get; set; }

    // 28.7 — the HIERARCHY, which the file has always carried and this loader always read past. Four of the
    // nine columns were bound, and `Type` was used for one thing only: setting is_billable = false on
    // chapters and blocks. The parent relationship was read on every load and discarded on every load, which
    // is why the indication check had to truncate a diagnosis to three characters and compare (design 44 §6).
    [Name("Parent_Code")] public string? ParentCode { get; set; }
    [Name("Chapter_Code")] public string? ChapterCode { get; set; }
    [Name("Block_Code")] public string? BlockCode { get; set; }
}

/// <summary>Raw Files/CPT 2022 Codes.csv (BOM on first header)</summary>
public sealed class CptCsvRow
{
    [Name("Code")] public string Code { get; set; } = "";
    [Name("Category")] public string? Category { get; set; }
    [Name("Description")] public string Description { get; set; } = "";
}

/// <summary>Raw Files/Egyptian Drugs - ATC Classified.csv (the ATC-bearing drug master)</summary>
public sealed class DrugCsvRow
{
    [Name("Commercial Name (EN)")] public string CommercialNameEn { get; set; } = "";
    [Name("Scientific Name")] public string? ScientificName { get; set; }
    [Name("Manufacturer")] public string? Manufacturer { get; set; }
    [Name("Drug Class")] public string? DrugClass { get; set; }
    [Name("Route")] public string? Route { get; set; }
    [Name("Price (EGP)")] public string? PriceEgp { get; set; }
    [Name("ATC Code")] public string? AtcCode { get; set; }
    [Name("ATC L1 – Anatomical Main Group")] public string? AtcL1 { get; set; }
    [Name("ATC L2 – Therapeutic Subgroup")] public string? AtcL2 { get; set; }
    [Name("ATC L3 – Pharmacological Subgroup")] public string? AtcL3 { get; set; }
    [Name("ATC L4 – Chemical Subgroup")] public string? AtcL4 { get; set; }
    [Name("ATC L5 – Chemical Substance")] public string? AtcL5 { get; set; }
}
