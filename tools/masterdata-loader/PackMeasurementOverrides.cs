using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;

namespace Mersal.MasterData.Loader;

/// <summary>One product's measurement, supplied because the workbook does not carry it.</summary>
/// <remarks>
/// Keyed on the workbook's own <c>ID</c>, which is stable across refreshes and is what <c>drug_id</c> is
/// derived from. Matching on the trade name would re-bind itself every time somebody fixed a spelling.
/// </remarks>
public sealed class PackMeasurementOverrideRow
{
    [Name("source_row_id")] public string SourceRowId { get; set; } = "";
    /// <summary>Carried for the reader, never matched on — see the class remarks.</summary>
    [Name("trade_name")] public string TradeName { get; set; } = "";
    [Name("volume_ml")] public string? VolumeMl { get; set; }
    [Name("iu_per_ml")] public string? IuPerMl { get; set; }
    /// <summary>Why this value is what it is. Required — see <see cref="PackMeasurementOverrides"/>.</summary>
    [Name("basis")] public string Basis { get; set; } = "";
}

/// <summary>
/// 31.3 — measurements the drug workbook omits, stated per product and subordinate to it.
///
/// ============================================================================================================
/// WHY A CURATED LIST RATHER THAN A RULE
/// ============================================================================================================
/// A box's contents in IU need a volume, and "Lantus Solostar 100 I.U./ML 5 Pens" states its concentration and
/// never its millilitres. Three millilitres is the standard fill of every marketed insulin pen and cartridge,
/// and writing THAT into <see cref="PackUnitRules"/> would be the guess invariant 8 exists to forbid: it would
/// apply itself to the next product that is not 3 ml, and a wrong box count is indistinguishable from a right
/// one at a dispensing counter.
///
/// A list of named products with a stated basis behaves differently in the one way that matters — it cannot
/// spread. A new insulin arriving in a workbook refresh matches nothing, derives no content, and appears in
/// the loader's missing-content report, which is exactly where a fact nobody has supplied belongs.
///
/// ============================================================================================================
/// THE WORKBOOK ALWAYS WINS
/// ============================================================================================================
/// These fill GAPS. Where the sheet states a volume — in its own column or in the product's name — the sheet's
/// value is used and the override is not consulted, so this file can never quietly contradict the catalogue.
/// Entries that matched nothing are reported at the end of a load: a curated list nobody prunes decays into a
/// list of things that used to be true.
/// </summary>
public sealed class PackMeasurementOverrides
{
    /// <summary>Where the shipped file lives, relative to the loader's own directory.</summary>
    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "data", "pack-measurement-overrides.csv");

    private readonly Dictionary<string, PackMeasurementOverrideRow> _byId;
    private readonly HashSet<string> _hit = new(StringComparer.Ordinal);

    private PackMeasurementOverrides(Dictionary<string, PackMeasurementOverrideRow> byId) => _byId = byId;

    /// <summary>An empty set — the loader's behaviour when no file is present.</summary>
    public static PackMeasurementOverrides None { get; } = new([]);

    public static PackMeasurementOverrides From(IEnumerable<PackMeasurementOverrideRow> rows) =>
        new(rows.Where(r => !string.IsNullOrWhiteSpace(r.SourceRowId))
               .ToDictionary(r => r.SourceRowId.Trim(), StringComparer.Ordinal));

    /// <summary>Reads the file, or returns <see cref="None"/> when it is absent.</summary>
    public static PackMeasurementOverrides Load(string path)
    {
        if (!File.Exists(path)) return None;

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectColumnCountChanges = false,
            MissingFieldFound = null,
            BadDataFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
        };
        using var csv = new CsvReader(new StreamReader(path), config);
        return From(csv.GetRecords<PackMeasurementOverrideRow>().ToList());
    }

    public int Count => _byId.Count;
    public IEnumerable<PackMeasurementOverrideRow> All => _byId.Values;

    /// <summary>The entry for a workbook row, and a note that it was consulted.</summary>
    public PackMeasurementOverrideRow? For(string? sourceRowId)
    {
        if (string.IsNullOrWhiteSpace(sourceRowId)) return null;
        if (!_byId.TryGetValue(sourceRowId.Trim(), out var row)) return null;
        _hit.Add(row.SourceRowId.Trim());
        return row;
    }

    /// <summary>Entries no workbook row matched — a list that has outlived its catalogue.</summary>
    public IReadOnlyList<PackMeasurementOverrideRow> Unused() =>
        [.. _byId.Values.Where(r => !_hit.Contains(r.SourceRowId.Trim()))];

    /// <summary>A number as the file writes it, or null. Never throws on a bad cell — the load reports it.</summary>
    public static decimal? Number(string? text) =>
        decimal.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : null;
}
