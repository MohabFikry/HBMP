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

    /// <summary>The result of loading the Egyptian drug-list workbook: drugs, ATC classes and indications.</summary>
    public sealed record DrugListLoad(
        LoadReport DrugReport,
        LoadReport AtcReport,
        LoadReport IndicationReport,
        List<AtcClass> Atc,
        List<Drug> Drugs,
        List<DrugIndication> Indications);

    /// <summary>
    /// Loads "Master Lists/egyptian-drug-list_5.xlsx" — drugs, their ATC classification, and the drug↔ICD
    /// indication link that does not exist anywhere else in the platform (doc 43 §6).
    /// </summary>
    /// <param name="knownIcdCodes">
    /// Every ICD code already loaded into <c>masterdata.icd_code</c>. Indication codes are validated against
    /// its 3-character categories and unmatched ones are <b>reported</b>, never silently dropped: a drug
    /// whose indications all failed to resolve produces "not checked" forever, and that has to be visible
    /// at load time rather than discovered by a prescriber.
    /// </param>
    public static DrugListLoad LoadDrugList(string path, string release, IEnumerable<string> knownIcdCodes)
    {
        var drugReport = new LoadReport("drug");
        var atcReport = new LoadReport("atc_class");
        var indicationReport = new LoadReport("drug_indication");

        var categories = knownIcdCodes
            .Select(MasterDataNormalize.IcdCategory)
            .ToHashSet(StringComparer.Ordinal);

        var drugsByCode = new Dictionary<string, Drug>(StringComparer.Ordinal);
        var atcByCode = new Dictionary<string, AtcClass>(StringComparer.Ordinal);
        var indications = new List<DrugIndication>();

        var unmatchedCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var drugsWithNoIndicationData = 0;
        var drugsLosingEveryIndication = new List<string>();
        var missingStrength = 0;

        foreach (var row in XlsxReader.ReadDrugList(path))
        {
            drugReport.Read++;
            if (string.IsNullOrWhiteSpace(row.TradeNameEn)) { drugReport.Skip("blank-trade-name"); continue; }
            if (string.IsNullOrWhiteSpace(row.SourceRowId)) { drugReport.Skip("blank-source-id"); continue; }

            var drug = Mappers.ToDrugFromXlsx(row, release);
            if (string.IsNullOrWhiteSpace(drug.DrugCode)) { drugReport.Skip("empty-drug-code"); continue; }
            drugsByCode[drug.DrugCode] = drug;
            if (drug.Strength is null) missingStrength++;

            foreach (var atc in Mappers.ToAtcClasses(row, release))
            {
                atcReport.Read++;
                if (atc.Level == 0) { atcReport.Skip("bad-atc-length"); continue; }
                atcByCode[atc.AtcCode] = atc;
            }

            var parsed = Mappers.ToDrugIndications(row, drug.DrugId, release).ToList();
            indicationReport.Read += parsed.Count;

            if (parsed.Count == 0) { drugsWithNoIndicationData++; continue; }

            var kept = 0;
            foreach (var indication in parsed)
            {
                if (!categories.Contains(indication.IcdCode))
                {
                    indicationReport.Skip("icd-unmatched");
                    unmatchedCodes[indication.IcdCode] = unmatchedCodes.GetValueOrDefault(indication.IcdCode) + 1;
                    continue;
                }
                indications.Add(indication);
                kept++;
            }

            // The dangerous case: the drug HAS indication data, but none of it resolved. Downstream this is
            // indistinguishable from "no data", so it is named here rather than left to be inferred.
            if (kept == 0) drugsLosingEveryIndication.Add($"{drug.DrugCode} ({string.Join('/', parsed.Select(p => p.IcdCode))})");
        }

        foreach (var d in drugsByCode.Values)
        {
            if (d.AtcCode is not null && !atcByCode.ContainsKey(d.AtcCode))
            {
                drugReport.Skip("atc-unmatched(kept, null-linked)");
                d.AtcCode = null;
            }
        }

        drugReport.FinalCount = drugsByCode.Count;
        atcReport.FinalCount = atcByCode.Count;
        indicationReport.FinalCount = indications.Count;

        drugReport.Note($"name_ar: 0/{drugsByCode.Count} — the workbook carries no Arabic trade name; the combobox falls back to the English name.");
        drugReport.Note($"strength: {drugsByCode.Count - missingStrength}/{drugsByCode.Count} populated (from 'Strength', falling back to 'Volume / Weight').");

        indicationReport.Note($"{drugsWithNoIndicationData} drug(s) carry no indication data — these report \"not checked\", never \"OK\".");
        if (unmatchedCodes.Count > 0)
        {
            var top = unmatchedCodes.OrderByDescending(kv => kv.Value).Take(20).Select(kv => $"{kv.Key}×{kv.Value}");
            indicationReport.Note($"{unmatchedCodes.Count} distinct ICD category/ies did not resolve against masterdata.icd_code: {string.Join(", ", top)}");
        }
        if (drugsLosingEveryIndication.Count > 0)
        {
            indicationReport.Note(
                $"{drugsLosingEveryIndication.Count} drug(s) had indication data where NONE of it resolved — they will report " +
                $"\"not checked\" indefinitely: {string.Join("; ", drugsLosingEveryIndication.Take(10))}" +
                (drugsLosingEveryIndication.Count > 10 ? ", …" : ""));
        }

        return new DrugListLoad(
            drugReport, atcReport, indicationReport,
            atcByCode.Values.OrderBy(a => a.Level).ToList(),
            drugsByCode.Values.ToList(),
            indications);
    }
}
