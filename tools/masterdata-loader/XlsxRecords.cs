namespace Mersal.MasterData.Loader;

/// <summary>
/// One row of "Master Lists/egyptian-drug-list_5.xlsx", sheet <c>Drug List</c> (33 columns, 22,653 rows).
/// </summary>
/// <remarks>
/// Column-by-column provenance lives in the loader README. Only the columns the platform actually uses are
/// modelled; barcode/origin/pack-size columns are read past deliberately rather than carried as dead fields.
/// </remarks>
public sealed class DrugListXlsxRow
{
    public string? SourceRowId { get; set; }            // A  "ID"                    100%
    public string? TradeNameEn { get; set; }            // B  "Trade Name (EN)"       100%
    public string? PriceEgp { get; set; }               // C  "Price (EGP)"           100%
    public string? ActiveIngredient { get; set; }       // D  "Active Ingredient"      95.3%
    public string? Manufacturer { get; set; }           // E  "Manufacturer"           98.5%
    public string? AtcCode { get; set; }                // H  "ATC Code"               85.2%
    public string? AtcL1 { get; set; }                  // J  "ATC L1 Name"
    public string? AtcL2 { get; set; }                  // L  "ATC L2 Name"
    public string? AtcL3 { get; set; }                  // N  "ATC L3 Name"
    public string? AtcL4 { get; set; }                  // P  "ATC L4 Name"
    public string? AtcL5 { get; set; }                  // R  "ATC L5 Name"
    public string? RelatedIcds { get; set; }            // T  "Related ICDs"          100%  ← the indications
    public string? IcdCount { get; set; }               // U  "ICD Count"             100%  (checksum for T)
    public string? IcdBasis { get; set; }               // V  "ICD Basis"             100%  ← per-row provenance
    // 29.6 (design 45 §6) — the pack facts. Previously "read past deliberately rather than carried as dead
    // fields"; they stop being dead the moment a quantity has to be converted into whole packs.
    public string? MajorUnits { get; set; }             // W  "Major Units (per box)"  — strips/blisters per box
    public string? MinorUnits { get; set; }             // X  "Minor Units (total)"    — PRESCRIBING units per box
    public string? VolumeWeight { get; set; }           // Y  "Volume / Weight"        33.3%
    public string? Strength { get; set; }               // Z  "Strength"               60.4%
    public string? DosageForm { get; set; }             // AA "Dosage Form"            98.7%
}

/// <summary>The workbook header names this loader binds to, kept in one place so a rename fails loudly.</summary>
public static class DrugListColumns
{
    public const string SourceRowId = "ID";
    public const string TradeNameEn = "Trade Name (EN)";
    public const string PriceEgp = "Price (EGP)";
    public const string ActiveIngredient = "Active Ingredient";
    public const string Manufacturer = "Manufacturer";
    public const string AtcCode = "ATC Code";
    public const string AtcL1 = "ATC L1 Name";
    public const string AtcL2 = "ATC L2 Name";
    public const string AtcL3 = "ATC L3 Name";
    public const string AtcL4 = "ATC L4 Name";
    public const string AtcL5 = "ATC L5 Name";
    public const string RelatedIcds = "Related ICDs";
    public const string IcdCount = "ICD Count";
    public const string IcdBasis = "ICD Basis";
    public const string MajorUnits = "Major Units (per box)";
    public const string MinorUnits = "Minor Units (total)";
    public const string VolumeWeight = "Volume / Weight";
    public const string Strength = "Strength";
    public const string DosageForm = "Dosage Form";

    /// <summary>Every column the loader requires. Absence is fatal — see <c>XlsxReader.ReadDrugList</c>.</summary>
    public static readonly string[] Required =
    [
        SourceRowId, TradeNameEn, PriceEgp, ActiveIngredient, Manufacturer, AtcCode,
        AtcL1, AtcL2, AtcL3, AtcL4, AtcL5, RelatedIcds, IcdCount, IcdBasis,
        MajorUnits, MinorUnits, VolumeWeight, Strength, DosageForm,
    ];
}
