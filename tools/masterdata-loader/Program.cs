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

if (useDrugList)
{
    Console.WriteLine($"drug source: {drugListPath} (workbook — includes indications)");
    var load = Loaders.LoadDrugList(drugListPath, release, icd.Select(i => i.Code));
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
