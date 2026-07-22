using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Loader;

/// <summary>
/// Reads the real reference files, maps + normalizes + dedupes on the natural key, and reports counts.
/// In DB mode it upserts (insert-or-update by natural key) so a second run is idempotent. In dry-run it
/// parses + validates + counts only (no writes) — used to prove ingestion without a database.
/// </summary>
public static class Loaders
{
    private static CsvReader OpenCsv(string path)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectColumnCountChanges = false,
            MissingFieldFound = null,   // tolerate ragged rows
            BadDataFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
        };
        return new CsvReader(new StreamReader(path), config);
    }

    public static (LoadReport, List<IcdCode>) LoadIcd(string path, string release)
    {
        var report = new LoadReport("icd_code");
        var byCode = new Dictionary<string, IcdCode>(StringComparer.Ordinal);
        using var csv = OpenCsv(path);
        foreach (var row in csv.GetRecords<IcdCsvRow>())
        {
            report.Read++;
            if (string.IsNullOrWhiteSpace(row.Code)) { report.Skip("blank-code"); continue; }
            var e = Mappers.ToIcd(row, release);
            byCode[e.Code] = e; // last-wins dedupe on natural key
        }
        report.FinalCount = byCode.Count;
        return (report, byCode.Values.ToList());
    }

    public static (LoadReport, List<CptCode>) LoadCpt(string path, string release)
    {
        var report = new LoadReport("cpt_code");
        var byCode = new Dictionary<string, CptCode>(StringComparer.Ordinal);
        using var csv = OpenCsv(path);
        foreach (var row in csv.GetRecords<CptCsvRow>())
        {
            report.Read++;
            if (string.IsNullOrWhiteSpace(row.Code)) { report.Skip("blank-code"); continue; }
            var e = Mappers.ToCpt(row, release);
            byCode[e.Code] = e;
        }
        report.FinalCount = byCode.Count;
        return (report, byCode.Values.ToList());
    }

    /// <summary>
    /// Loads drugs AND derives the ATC classification from the same file (ATC codes + level titles),
    /// so drugs link to atc_class consistently. Returns ATC classes (deduped) + drugs.
    /// </summary>
    public static (LoadReport DrugReport, LoadReport AtcReport, List<AtcClass> Atc, List<Drug> Drugs)
        LoadDrugsAndAtc(string path, string release)
    {
        var drugReport = new LoadReport("drug");
        var atcReport = new LoadReport("atc_class");
        var drugsByCode = new Dictionary<string, Drug>(StringComparer.Ordinal);
        var atcByCode = new Dictionary<string, AtcClass>(StringComparer.Ordinal);

        using var csv = OpenCsv(path);
        foreach (var row in csv.GetRecords<DrugCsvRow>())
        {
            drugReport.Read++;
            if (string.IsNullOrWhiteSpace(row.CommercialNameEn)) { drugReport.Skip("blank-name"); continue; }

            var drug = Mappers.ToDrug(row, release);
            if (string.IsNullOrWhiteSpace(drug.DrugCode)) { drugReport.Skip("empty-drug-code"); continue; }
            drugsByCode[drug.DrugCode] = drug; // dedupe on natural key

            foreach (var atc in Mappers.ToAtcClasses(row, release))
            {
                atcReport.Read++;
                if (atc.Level == 0) { atcReport.Skip("bad-atc-length"); continue; }
                atcByCode[atc.AtcCode] = atc;
            }
        }

        // Unmatched ATC on a drug is logged, not fatal (load atc first, then drugs link where possible).
        foreach (var d in drugsByCode.Values)
        {
            if (d.AtcCode is not null && !atcByCode.ContainsKey(d.AtcCode))
            {
                drugReport.Skip("atc-unmatched(kept, null-linked)");
                d.AtcCode = null; // keep the drug, drop the dangling FK
            }
        }

        drugReport.FinalCount = drugsByCode.Count;
        atcReport.FinalCount = atcByCode.Count;
        return (drugReport, atcReport, atcByCode.Values.OrderBy(a => a.Level).ToList(), drugsByCode.Values.ToList());
    }
}
