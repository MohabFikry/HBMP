using System.Security.Cryptography;
using Mersal.Audit.Client;
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

foreach (var (label, path) in new[] { ("ICD", icdPath), ("CPT", cptPath), ("Drugs", drugPath) })
{
    if (!File.Exists(path)) { Console.Error.WriteLine($"MISSING {label} file: {path}"); return 2; }
}

Console.WriteLine($"== masterdata-loader ==  release={release}  mode={(dryRun ? "DRY-RUN (no writes)" : "DB upsert")}");
Console.WriteLine($"data-dir: {dataDir}\n");

// --- Parse + map + dedupe the real files ---
var (icdReport, icd) = Loaders.LoadIcd(icdPath, release);
var (cptReport, cpt) = Loaders.LoadCpt(cptPath, release);
var (drugReport, atcReport, atc, drugs) = Loaders.LoadDrugsAndAtc(drugPath, release);

var reports = new[] { icdReport, cptReport, atcReport, drugReport };

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
    await DbUpsert.UpsertCptAsync(db, cpt, cptReport, default);
    await DbUpsert.UpsertAtcAsync(db, atc, atcReport, default);      // parents-before-children, before drugs
    await DbUpsert.UpsertDrugsAsync(db, drugs, drugReport, default);
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
        drugs = new { drugReport.Read, drugReport.FinalCount, sha = Sha(drugPath) },
        atc = new { atcReport.FinalCount },
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
