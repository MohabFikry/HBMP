using System.Security.Cryptography;
using Mersal.Audit.Client;
using Mersal.MasterData.Domain;
using Mersal.MasterData.Infrastructure;
using Mersal.MasterData.Loader;
using Microsoft.EntityFrameworkCore;

// masterdata-loader — ingests the client's REAL reference data (phase-0b §0b.2).
// Idempotent, versioned by --release, audited, reversible. Dry-run parses + reports without a DB.
//
// Usage:
//   masterdata-loader --dry-run [--data-dir <path>] [--release <id>]
//   masterdata-loader --connection "Host=...;Database=hbmp;Username=...;Password=..." [--data-dir <path>] [--release <id>]

var argList = args.ToList();
bool dryRun = argList.Contains("--dry-run");
string Arg(string name, string dflt) { var i = argList.IndexOf(name); return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : dflt; }

var dataDir = Arg("--data-dir", FindDataDir());
var release = Arg("--release", $"load-{DateTime.UtcNow:yyyyMMdd}");
var connection = Arg("--connection", Environment.GetEnvironmentVariable("MASTERDATA_DB") ?? "");

var icdPath = Path.Combine(dataDir, "Raw Files", "ICD10_2019_full.csv");
var cptPath = Path.Combine(dataDir, "Raw Files", "CPT 2022 Codes.csv");
var drugPath = Path.Combine(dataDir, "Raw Files", "Egyptian Drugs - ATC Classified.csv");

// The workbook supersedes the CSV: it carries the drug↔ICD indication link, strength and dosage form,
// and a stable per-row id. The CSV remains the fallback so the loader still runs where the workbook is
// absent — but without it there are no indications, and the indication check reports "not checked".
var drugListPath = Path.Combine(dataDir, "Master Lists", "egyptian-drug-list_5.xlsx");
var useDrugList = File.Exists(drugListPath);

foreach (var (label, path) in new[] { ("ICD", icdPath), ("CPT", cptPath) })
{
    if (!File.Exists(path)) { Console.Error.WriteLine($"MISSING {label} file: {path}"); return 2; }
}
if (!useDrugList && !File.Exists(drugPath))
{
    Console.Error.WriteLine($"MISSING drug source: neither {drugListPath} nor {drugPath}");
    return 2;
}

Console.WriteLine($"== masterdata-loader ==  release={release}  mode={(dryRun ? "DRY-RUN (no writes)" : "DB upsert")}");
Console.WriteLine($"data-dir: {dataDir}\n");

// --- Parse + map + dedupe the real files ---
var (icdReport, icd) = Loaders.LoadIcd(icdPath, release);
var (cptReport, cpt) = Loaders.LoadCpt(cptPath, release);

LoadReport drugReport, atcReport;
List<Mersal.MasterData.Domain.AtcClass> atc;
List<Mersal.MasterData.Domain.Drug> drugs;
List<Mersal.MasterData.Domain.DrugIndication> indications = [];
LoadReport? indicationReport = null;
var packOverrides = PackMeasurementOverrides.None;

if (useDrugList)
{
    Console.WriteLine($"drug source: {drugListPath} (workbook — includes indications)");
    // 31.3 — measurements the workbook omits, read from a file a pharmacist can open. Subordinate to the
    // sheet: they fill silences and never contradict it. Absent file ⇒ empty set ⇒ nothing changes.
    packOverrides = PackMeasurementOverrides.Load(PackMeasurementOverrides.DefaultPath);
    Console.WriteLine($"pack-measurement overrides: {packOverrides.Count} product(s) "
                    + $"from {PackMeasurementOverrides.DefaultPath}");

    var load = Loaders.LoadDrugList(drugListPath, release, icd.Select(i => i.Code), packOverrides);
    (drugReport, atcReport, indicationReport, atc, drugs, indications) =
        (load.DrugReport, load.AtcReport, load.IndicationReport, load.Atc, load.Drugs, load.Indications);
}
else
{
    Console.WriteLine($"drug source: {drugPath} (legacy CSV — NO indication data; the indication check will report \"not checked\")");
    (drugReport, atcReport, atc, drugs) = Loaders.LoadDrugsAndAtc(drugPath, release);
}

// 28.1 — products resolved into the molecules they contain. Derived here rather than inside the write
// branch so a DRY RUN reports it: a report that silently omits a table is how a load looks complete while a
// clinical check goes on finding nothing.
var ingredientReport = new LoadReport("ingredient");
var linkReport = new LoadReport("drug_ingredient");
var allIngredients = new List<Mersal.MasterData.Domain.Ingredient>();
var allLinks = new List<Mersal.MasterData.Domain.DrugIngredient>();
var unresolvedDrugs = 0;

foreach (var drug in drugs)
{
    var (ing, links) = Mappers.ToIngredientLinks(drug, release);
    if (links.Count == 0) unresolvedDrugs++;
    allIngredients.AddRange(ing);
    allLinks.AddRange(links);
}

ingredientReport.Read = allIngredients.Count;
ingredientReport.FinalCount = allIngredients.Select(i => i.IngredientKey).Distinct(StringComparer.Ordinal).Count();
linkReport.Read = allLinks.Count;
linkReport.FinalCount = allLinks.Count;
linkReport.Note(
    $"{unresolvedDrugs} product(s) resolved to NO molecule — no usable scientific_name. The ingredient-level "
    + "checks report these rather than passing them.");
linkReport.Note(
    $"{allLinks.GroupBy(l => l.DrugId).Count(g => g.Count() > 1)} product(s) are COMBINATIONS and decompose "
    + "into more than one molecule — the case a product-keyed rule cannot express.");

LoadReport[] reports = indicationReport is null
    ? [icdReport, cptReport, atcReport, drugReport, ingredientReport, linkReport]
    : [icdReport, cptReport, atcReport, drugReport, ingredientReport, linkReport, indicationReport];

if (!dryRun)
{
    if (string.IsNullOrWhiteSpace(connection))
    {
        Console.Error.WriteLine("No --connection (or MASTERDATA_DB) provided for DB upsert. Use --dry-run to parse only.");
        return 2;
    }
    var options = new DbContextOptionsBuilder<MasterDataDbContext>().UseNpgsql(connection).UseSnakeCaseNamingConvention().Options;
    await using var db = new MasterDataDbContext(options);
    var migrationsDir = Path.Combine(AppContext.BaseDirectory, "Migrations");
    await DbUpsert.ApplyMigrationAsync(db, migrationsDir, default);

    await DbUpsert.UpsertIcdAsync(db, icd, icdReport, default);

    // 28.7 — the ancestor closure, derived from the parent chain the upsert above just wrote. Without this
    // the hierarchy is loaded and unusable: the descendant-or-self lookup the indication and
    // contraindication checks make reads icd_ancestor, not icd_code.parent_code.
    await DbUpsert.RebuildIcdAncestorsAsync(db, default);
    Console.WriteLine("icd_ancestor: closure rebuilt from parent_code");
    await DbUpsert.UpsertCptAsync(db, cpt, cptReport, default);
    await DbUpsert.UpsertAtcAsync(db, atc, atcReport, default);      // parents-before-children, before drugs
    await DbUpsert.UpsertDrugsAsync(db, drugs, drugReport, default);
    // Casing over the WHOLE table, not only this load's rows — the catalogue holds products from two files
    // that disagree about capitals, and they sit in the same list. Before the price recompute, which reads
    // the scientific name to group comparable products.
    await DbUpsert.RecaseDrugNamesAsync(db, drugReport, default);
    // 29.7 — DERIVED, never authored: recomputed on every load, right after the prices land.
    await DbUpsert.RecomputeLowestPriceAsync(db, DateTimeOffset.UtcNow, drugReport, default);
    // A drug whose uuid was ADOPTED from an earlier load keeps its own id, so the links — built against the
    // id derived from the source row — have to be re-pointed at what was actually persisted. Same step the
    // indications take below, and for the same reason: the foreign key is real.
    var derivedToPersistedDrug = drugs
        .Where(d => d.SourceRowId is not null)
        .ToDictionary(d => MasterDataNormalize.DrugId(d.SourceRowId!), d => d.DrugId);
    foreach (var link in allLinks)
    {
        if (derivedToPersistedDrug.TryGetValue(link.DrugId, out var persisted)) link.DrugId = persisted;
    }

    await DbUpsert.UpsertIngredientsAsync(db, allIngredients, ingredientReport, default);
    await DbUpsert.UpsertDrugIngredientsAsync(db, allLinks, linkReport, default);

    if (indicationReport is not null)
    {
        // After drugs, so every indication's drug_id FK resolves. UpsertDrugsAsync may have adopted a row
        // that already existed and kept ITS uuid, so re-point the indications — which were built against the
        // id derived from the source row — at the ids actually persisted.
        var derivedToPersisted = drugs
            .Where(d => d.SourceRowId is not null)
            .ToDictionary(d => MasterDataNormalize.DrugId(d.SourceRowId!), d => d.DrugId);
        foreach (var i in indications)
        {
            if (derivedToPersisted.TryGetValue(i.DrugId, out var persisted)) i.DrugId = persisted;
        }

        await DbUpsert.UpsertDrugIndicationsAsync(db, indications, indicationReport, default);
    }
}

// --- Load report (stdout + file) ---
Console.WriteLine("---- LOAD REPORT ----");
foreach (var r in reports) Console.WriteLine(r);

var reportDir = Path.Combine(AppContext.BaseDirectory, "reports");
Directory.CreateDirectory(reportDir);
var reportPath = Path.Combine(reportDir, $"load-report-{release}.txt");
await File.WriteAllLinesAsync(reportPath, reports.Select(r => r.ToString()));
Console.WriteLine($"\nreport written: {reportPath}");

// --- 29.6: pack-data coverage (design 45 §6) -----------------------------------------------------------
// "Rows missing a required field set unit_data_incomplete and are LISTED in the load report — not silently
// defaulted." The counts are the report; the point of printing them is that a drop in coverage after a
// workbook refresh is visible rather than discovered as a NotChecked at a dispensing counter.
{
    var withUnit = drugs.Count(d => !string.IsNullOrWhiteSpace(d.PrescribingUnit));
    var withPack = drugs.Count(d => d.PackSize is > 0);
    var withContent = drugs.Count(d => d.PackContent is > 0);
    var withSplit = drugs.Count(d => d.IsPackSplittable is not null);
    var complete = drugs.Count(d => !d.UnitDataIncomplete);
    var total = drugs.Count;
    string Pct(int n) => total == 0 ? "n/a" : $"{100.0 * n / total:F1}%";

    Console.WriteLine();
    Console.WriteLine("=== 29.6 / 31.3 pack-data coverage (design 45 §6) ===");
    Console.WriteLine($"  prescribing_unit    {withUnit,7:N0} / {total:N0}  ({Pct(withUnit)})");
    Console.WriteLine($"  pack_size           {withPack,7:N0} / {total:N0}  ({Pct(withPack)})");
    Console.WriteLine($"  pack_content        {withContent,7:N0} / {total:N0}  ({Pct(withContent)})  ← the divisor");
    Console.WriteLine($"  is_pack_splittable  {withSplit,7:N0} / {total:N0}  ({Pct(withSplit)})");
    Console.WriteLine($"  ALL THREE (usable)  {complete,7:N0} / {total:N0}  ({Pct(complete)})");
    Console.WriteLine($"  unit_data_incomplete{total - complete,7:N0} — these report NotChecked NAMING the missing field,");
    Console.WriteLine( "                               never a guessed quantity (invariant 8).");

    /*
     * 31.3 — THE ROWS ONE CELL SHORT OF A BOX COUNT, listed rather than counted.
     *
     * A product whose unit and splittability are known but whose CONTENT is not is a row where the workbook
     * has everything except the volume: "Lantus Solostar 100 I.U./ML 5 Pens" states its concentration and
     * omits how many millilitres a pen holds, so the box's contents in IU are unknowable and the composer
     * says so instead of dividing. These are worth naming because each is fixable by filling one cell, and
     * a percentage does not tell anybody which cell.
     */
    var oneCellShort = drugs
        .Where(d => !string.IsNullOrWhiteSpace(d.PrescribingUnit) && d.IsPackSplittable is not null
                    && d.PackContent is null)
        .OrderBy(d => d.Name, StringComparer.Ordinal)
        .ToList();

    /*
     * 31.3 — AN OVERRIDE THAT MATCHED NOTHING.
     *
     * The measurement file is keyed on the workbook's own row ids, so an entry that no row matched means the
     * catalogue moved on and the file did not — or that the sheet has since gained the column, in which case
     * the sheet won and the line is now dead weight. Either way it is reported: a curated list nobody prunes
     * decays into a list of things that used to be true, and silence is how that happens.
     */
    var strays = packOverrides.Unused();
    if (strays.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  !! {strays.Count} pack-measurement override(s) matched no workbook row:");
        foreach (var stray in strays) Console.WriteLine($"       {stray.SourceRowId,-8} {stray.TradeName}");
    }

    if (oneCellShort.Count > 0)
    {
        var contentPath = Path.Combine(reportDir, $"pack-content-missing-{release}.txt");
        await File.WriteAllLinesAsync(contentPath, oneCellShort.Select(d =>
            $"{d.SourceRowId,-8} {d.Name}  [unit={d.PrescribingUnit}, form={d.PackUnit}, strength={d.Strength}]"));
        Console.WriteLine();
        Console.WriteLine($"  {oneCellShort.Count:N0} products know their unit but not their box's contents —");
        Console.WriteLine( "  a 'Volume / Weight' away from a box count. Listed in:");
        Console.WriteLine($"    {contentPath}");
    }
}

// --- 29.2: CPT routing reconciliation (design 45 §2) ---------------------------------------------------
// "The routing map must be built from the LOADED VALUES and reconciled against the ranges above; where they
// disagree the range wins and the discrepancy is REPORTED rather than silently resolved."
//
// Emitted with the load rather than as a one-off script, because the thing it reconciles — which codes exist
// and what category they carry — changes every time the catalogue is reloaded. A reconciliation run once at
// design time answers for a catalogue that no longer exists.
{
    var routingReport = CptRoutingReconciliation.Build(cpt.Select(c => (c.Code, c.Category)));
    Console.WriteLine();
    Console.WriteLine(CptRoutingReconciliation.Format(routingReport));

    var routingPath = Path.Combine(reportDir, $"cpt-routing-reconciliation-{release}.txt");
    await File.WriteAllTextAsync(routingPath, CptRoutingReconciliation.Format(routingReport));
    Console.WriteLine($"routing reconciliation written: {routingPath}");
}

// --- Audit the load run (source files + checksums + counts + actor) via libs/audit-client ---
var outbox = new InMemoryAuditOutbox();
var audit = new AuditClient(outbox, new AuditClientContext("masterdata-loader"), TimeProvider.System);
await audit.EmitAsync(new AuditEventDraft
{
    EntityType = "masterdata.load", EntityId = release,
    Action = AuditAction.Create,
    ActorUserId = Environment.UserName,
    AfterState = System.Text.Json.JsonSerializer.Serialize(new
    {
        release, dryRun,
        icd = new { icdReport.Read, icdReport.FinalCount, sha = Sha(icdPath) },
        cpt = new { cptReport.Read, cptReport.FinalCount, sha = Sha(cptPath) },
        drugs = new
        {
            drugReport.Read, drugReport.FinalCount,
            source = useDrugList ? drugListPath : drugPath,
            sha = Sha(useDrugList ? drugListPath : drugPath),
        },
        atc = new { atcReport.FinalCount },
        indications = indicationReport is null
            ? null
            : new { indicationReport.Read, indicationReport.FinalCount, notes = indicationReport.Notes },
    }),
});
Console.WriteLine($"audit event staged: {outbox.Events[0].Action} masterdata.load/{release}");

return 0;

static string Sha(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()[..16];
}

static string FindDataDir()
{
    var dir = Directory.GetCurrentDirectory();
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        if (Directory.Exists(Path.Combine(dir, "Raw Files"))) return dir;
        dir = Directory.GetParent(dir)?.FullName;
    }
    return Directory.GetCurrentDirectory();
}
