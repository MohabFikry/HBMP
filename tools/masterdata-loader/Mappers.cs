using Mersal.Prescribing;
using Mersal.Ingredients;
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
        // 28.7 — the relationship the file carries and this loader used to discard. Without it the indication
        // check cannot express a BLOCK-level indication ("J00-J06") at all, cannot honour an indication more
        // specific than three characters, and reads a less-specific diagnosis as a mismatch rather than as
        // the open question it is.
        ParentCode = Clean(r.ParentCode) is { } parent ? MasterDataNormalize.Icd(parent) : null,
        NodeKind = NodeKindOf(r.Type),
        SourceRelease = release,
    };

    /// <summary>
    /// The source's <c>Type</c> column, mapped to the four levels of the ICD-10 tree.
    /// </summary>
    /// <remarks>
    /// An unrecognised value becomes <c>Subcategory</c> — the most specific level, and therefore the one that
    /// matches the least. Guessing upwards would make an unknown row behave like a block and silently widen
    /// every indication written against it.
    /// </remarks>
    private static string NodeKindOf(string? type) => (type?.Trim().ToLowerInvariant()) switch
    {
        "chapter" => "Chapter",
        "block" => "Block",
        "category" => "Category",
        _ => "Subcategory",
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
        // Derived, not random: a re-load of the same file must produce the same ids. See
        // MasterDataNormalize.DrugId — Guid.NewGuid() here used to make id stability an accident of the
        // upsert matching on an unchanged trade-name string.
        DrugId = MasterDataNormalize.DrugId(MasterDataNormalize.DrugCode(r.CommercialNameEn)),
        DrugCode = MasterDataNormalize.DrugCode(r.CommercialNameEn),
        // Cased for display at LOAD time, not in CSS — this source shouts every name in capitals and the
        // workbook whispers every one of them, and the two sit in the same list.
        Name = MasterDataNormalize.DisplayName(r.CommercialNameEn)!,
        ScientificName = MasterDataNormalize.DisplayName(Clean(r.ScientificName)),
        Manufacturer = Clean(r.Manufacturer),
        Form = Clean(r.Route),
        AtcCode = string.IsNullOrWhiteSpace(r.AtcCode) ? null : MasterDataNormalize.Atc(r.AtcCode),
        PriceEgp = MasterDataNormalize.Price(r.PriceEgp),
        SourceRelease = release,

        // 29.6 — the legacy CSV carries no pack columns, so unit data stays incomplete on this path and the
        // quantity check reports NotChecked. Better than a default: the CSV is the fallback source, and a
        // fallback that invented pack sizes would be worse than one that admits it has none.
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

    // ---------------------------------------------------------------- egyptian-drug-list_5.xlsx

    /// <summary>
    /// A filler ICD the source uses to mean "no indication is recorded for this product". The workbook drops
    /// it wherever a real indication exists, so it only ever appears alone. Loading it as a genuine
    /// indication would let a product with no clinical data read as checked — the exact "unavailable
    /// rendered as OK" failure this phase exists to prevent (doc 43 §1) — so it is never stored.
    /// </summary>
    public const string PlaceholderIcd = "Z76";

    public static Drug ToDrugFromXlsx(DrugListXlsxRow r, string release) => new()
    {
        DrugId = MasterDataNormalize.DrugId(r.SourceRowId!.Trim()),
        SourceRowId = r.SourceRowId!.Trim(),
        DrugCode = MasterDataNormalize.DrugCode(r.TradeNameEn!),
        // Cased for display at LOAD time — see MasterDataNormalize.DisplayName. The natural key above is
        // derived from the RAW name and upper-cases anyway, so re-casing adopts the existing row rather than
        // inserting a second copy of every drug.
        Name = MasterDataNormalize.DisplayName(r.TradeNameEn)!,
        // The workbook carries no Arabic trade name, so name_ar stays null and the UI falls back to the
        // English name rather than rendering an empty option. Documented in the loader README.
        NameAr = null,
        ScientificName = MasterDataNormalize.DisplayName(Clean(r.ActiveIngredient)),
        Manufacturer = Clean(r.Manufacturer),
        Form = Clean(r.DosageForm),
        // "Strength" is populated on 60.4% of rows; "Volume / Weight" covers a further slice of liquids
        // (120 ml, 30 gm) that is the same fact for a prescriber's purposes.
        Strength = Clean(r.Strength) ?? Clean(r.VolumeWeight),
        AtcCode = string.IsNullOrWhiteSpace(r.AtcCode) ? null : MasterDataNormalize.Atc(r.AtcCode),
        PriceEgp = MasterDataNormalize.Price(r.PriceEgp),
        SourceRelease = release,

        // ---- 29.6 — the pack facts (design 45 §6) ------------------------------------------------------
        //
        // BOTH unit columns, resolved together. `pack_size` is "Minor Units (total)" — X — NOT "Major Units
        // (per box)" — W: W is strips/blisters per box, so a 20-tablet pack is 2 strips of 10 and mapping W
        // would make every tablet quantity out by a factor of ten. The two columns are adjacent and similarly
        // named, which is exactly why this is written down rather than left to the reader.
        //
        // SPLITTABILITY now comes from the same pair rather than from the dosage form, because the form is
        // wrong in both directions on real rows — it calls a box of three ampoules unsplittable, and it has
        // nothing at all to say about the 38 forms it does not recognise. See PackUnitRules.FromPackUnits.
        PackSize = PackFactsOf(r).PackSize,
        PackUnit = Clean(r.DosageForm),
        PrescribingUnit = PackFactsOf(r).PrescribingUnit,
        IsPackSplittable = PackFactsOf(r).IsPackSplittable,
        // Rows missing ANY of the three are flagged and LISTED in the load report — "not silently defaulted".
        UnitDataIncomplete = !PackUnitRules.IsComplete(
            PackFactsOf(r).PrescribingUnit, PackFactsOf(r).PackSize, PackFactsOf(r).IsPackSplittable),
    };

    /// <summary>
    /// The three pack facts for one workbook row, from every source in order of authority.
    /// </summary>
    /// <remarks>
    /// The workbook records no product-level override of either fact today, so both stated arguments are
    /// null — they are passed explicitly rather than omitted because the override is the design's stated
    /// intent ("overridable per product") and the seam it will arrive through is this one.
    /// </remarks>
    public static DerivedPackFacts PackFactsOf(DrugListXlsxRow r) => PackUnitRules.Resolve(
        form: r.DosageForm,
        statedSplittable: null,
        statedUnit: null,
        majorUnits: MasterDataNormalize.Price(r.MajorUnits),
        minorUnits: MasterDataNormalize.Price(r.MinorUnits));

    /// <summary>ATC classification nodes from an xlsx row — same truncation rule as the CSV path.</summary>
    public static IEnumerable<AtcClass> ToAtcClasses(DrugListXlsxRow r, string release)
        => ToAtcClasses(
            new DrugCsvRow
            {
                CommercialNameEn = r.TradeNameEn ?? "",
                AtcCode = r.AtcCode ?? "",
                AtcL1 = r.AtcL1, AtcL2 = r.AtcL2, AtcL3 = r.AtcL3, AtcL4 = r.AtcL4, AtcL5 = r.AtcL5,
            },
            release);

    /// <summary>
    /// The drug's listed indications, as distinct ICD-10 <b>categories</b>.
    /// </summary>
    /// <remarks>
    /// Returns empty for a product whose only listed code is the <see cref="PlaceholderIcd"/> filler. Empty
    /// is a meaningful answer downstream: it makes the indication check report "not checked", which is not
    /// the same as, and must never render as, "OK".
    /// </remarks>
    public static IEnumerable<DrugIndication> ToDrugIndications(DrugListXlsxRow r, Guid drugId, string release)
    {
        if (string.IsNullOrWhiteSpace(r.RelatedIcds)) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var codes = r.RelatedIcds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MasterDataNormalize.IcdCategory)
            .Where(c => c.Length > 0)
            .ToList();

        if (codes.All(c => c == PlaceholderIcd)) yield break;

        var source = Clean(r.IcdBasis) ?? "unspecified";
        foreach (var code in codes)
        {
            if (code == PlaceholderIcd || !seen.Add(code)) continue;
            yield return new DrugIndication
            {
                // Derived from (drug, code) so a reload updates in place instead of duplicating.
                IndicationId = MasterDataNormalize.DrugId($"indication:{drugId}:{code}"),
                DrugId = drugId,
                IcdCode = code,
                IsPrimary = false,   // the source expresses no ranking; inventing one would be fabrication
                Source = source,
                SourceRelease = release,
            };
        }
    }

    /// <summary>
    /// The molecules a product contains, as <c>ingredient</c> rows and the links joining them to the drug.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where a catalogue of 22,653 trade names becomes something a clinical rule can be written
    /// against. An allergy, an interaction, a duplicate-therapy check and a dosing rule are all questions
    /// about MOLECULES; keying any of them on a product id needs one row per pair of brands, which is why
    /// <c>drug_interaction</c> held zero rows and would have stayed empty.
    /// </para>
    /// <para>
    /// A COMBINATION PRODUCT YIELDS SEVERAL LINKS — that is the point of the table, and the only reason
    /// co-amoxiclav screens as amoxicillin and the paracetamol inside a cold-and-flu remedy is findable.
    /// </para>
    /// <para>
    /// A product with no usable <c>scientific_name</c> yields NOTHING, and that absence is load-bearing:
    /// the ingredient-level checks report a medicine they could not resolve rather than passing it.
    /// </para>
    /// </remarks>
    public static (List<Ingredient> Ingredients, List<DrugIngredient> Links) ToIngredientLinks(
        Drug drug, string release)
    {
        ArgumentNullException.ThrowIfNull(drug);

        var ingredients = new List<Ingredient>();
        var links = new List<DrugIngredient>();

        var keys = IngredientTokens.Components(drug.ScientificName);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var now = DateTimeOffset.UtcNow;

            ingredients.Add(new Ingredient
            {
                // Derived from the key, so a reload produces the same id rather than a fresh one that
                // orphans every rule pointing at it — the same discipline MasterDataNormalize.DrugId
                // applies to products.
                IngredientId = MasterDataNormalize.DrugId($"ingredient:{key}"),
                IngredientKey = key,
                NameEn = Title(key),
                // No Arabic and no ATC from this source. Both are populated on the curated rows seeded by
                // migration 0009/0010, and the upsert never overwrites one of those with a derived row.
                NameAr = null,
                AtcCode = null,
                IsActive = true,
                Source = "derived from drug.scientific_name",
                SourceRelease = release,
                CreatedAt = now,
                UpdatedAt = now,
            });

            links.Add(new DrugIngredient
            {
                DrugId = drug.DrugId,
                IngredientKey = key,
                // Position as the source lists it. No clinical ranking is implied — the source expresses
                // none, and inventing one would be fabrication.
                Ordinal = i,
                Strength = drug.Strength,
                SourceRelease = release,
            });
        }

        return (ingredients, links);
    }

    /// <summary>"amoxicillin" → "Amoxicillin". A display name, never the key.</summary>
    private static string Title(string key) =>
        key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..];

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
