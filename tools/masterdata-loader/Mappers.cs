using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Loader;

/// <summary>Pure row → entity mapping (unit-testable, no IO). Mapping rationale in the loader README.</summary>
public static class Mappers
{
    // ICD Type values that are grouping nodes, not billable diagnoses.
    private static readonly HashSet<string> NonBillableTypes =
        new(StringComparer.OrdinalIgnoreCase) { "chapter", "block" };

    public static IcdCode ToIcd(IcdCsvRow r, string release) => new()
    {
        Code = MasterDataNormalize.Icd(r.Code),
        Title = r.Description.Trim(),
        Chapter = r.ChapterDescription?.Trim(),
        // Leaf/category codes are billable; chapters and blocks are grouping nodes.
        IsBillable = !NonBillableTypes.Contains(r.Type?.Trim() ?? ""),
        SourceRelease = release,
    };

    public static CptCode ToCpt(CptCsvRow r, string release) => new()
    {
        Code = MasterDataNormalize.Cpt(r.Code),
        Description = r.Description.Trim(),
        Category = string.IsNullOrWhiteSpace(r.Category) ? null : r.Category.Trim(),
        SourceRelease = release,
    };

    public static Drug ToDrug(DrugCsvRow r, string release) => new()
    {
        DrugId = Guid.NewGuid(),
        DrugCode = MasterDataNormalize.DrugCode(r.CommercialNameEn),
        Name = r.CommercialNameEn.Trim(),
        ScientificName = Clean(r.ScientificName),
        Manufacturer = Clean(r.Manufacturer),
        Form = Clean(r.Route),
        AtcCode = string.IsNullOrWhiteSpace(r.AtcCode) ? null : MasterDataNormalize.Atc(r.AtcCode),
        PriceEgp = MasterDataNormalize.Price(r.PriceEgp),
        SourceRelease = release,
    };

    /// <summary>
    /// Derive ATC classification nodes from a drug row's ATC Code (L5) + the L1–L5 title columns.
    /// Yields every level present (ancestor codes by truncation, titles from the matching column).
    /// Deduped by the caller on atc_code. This keeps atc_class consistent with referenced drug codes.
    /// </summary>
    public static IEnumerable<AtcClass> ToAtcClasses(DrugCsvRow r, string release)
    {
        if (string.IsNullOrWhiteSpace(r.AtcCode)) yield break;
        var full = MasterDataNormalize.Atc(r.AtcCode);

        // (code, title) per level, using the level-specific title columns when available.
        var levels = new (int Len, string? Title)[]
        {
            (1, r.AtcL1), (3, r.AtcL2), (4, r.AtcL3), (5, r.AtcL4), (7, r.AtcL5),
        };
        foreach (var (len, title) in levels)
        {
            if (full.Length < len) continue;
            var code = full[..len];
            yield return new AtcClass
            {
                AtcCode = code,
                Title = string.IsNullOrWhiteSpace(title) ? code : title.Trim(),
                Level = MasterDataNormalize.AtcLevel(code),
                SourceRelease = release,
            };
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
