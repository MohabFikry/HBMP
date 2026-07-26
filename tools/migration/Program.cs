using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Mersal.Migration.Core;
using Mersal.Migration.Db;
using Mersal.Migration.Streams;

// Mersal HBMP migration toolkit (phase 12.1). Reversible, audited, idempotent onboarding pipelines.
// Every run in a lower environment MUST use masked data (../25 §1); production runs are gate-guarded.

if (args.Length == 0) { PrintUsage(); return 0; }

try
{
    switch (args[0])
    {
        case "default-config": return DefaultConfig(Arg(args, "--stream") ?? "providers");
        case "run-providers": return await RunProviders(args);
        case "run-beneficiaries": return await RunBeneficiaries(args);
        case "rollback": return await Rollback(args);
        default: PrintUsage(); return 1;
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException or FileNotFoundException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int DefaultConfig(string stream)
{
    Console.WriteLine(DefaultConfigs.For(stream).ToJson());
    return 0;
}

static async Task<int> RunProviders(string[] args)
{
    var (sink, batch, config, rows, audit) = await Setup(args, "providers");
    var (recon, users) = await new ProviderStream(sink, audit, TimeProvider.System).RunAsync(batch, config, rows);
    Console.WriteLine(recon);
    Console.WriteLine($"loaded {users.Count} provider users — verify isolation before enabling (../11).");
    Guard(recon);
    return recon.Balances ? 0 : 3;
}

static async Task<int> RunBeneficiaries(string[] args)
{
    var (sink, batch, config, rows, audit) = await Setup(args, "beneficiaries");
    var (recon, dedupe) = await new BeneficiaryStream(sink, audit, TimeProvider.System).RunAsync(batch, config, rows, []);
    Console.WriteLine(recon);
    Console.WriteLine(dedupe);
    if (dedupe.QueuedForReview.Count > 0)
        Console.WriteLine($"HOLD: {dedupe.QueuedForReview.Count} ambiguous pairs need human sign-off before promotion.");
    Guard(recon);
    return recon.Balances ? 0 : 3;
}

static async Task<int> Rollback(string[] args)
{
    var conn = Require(args, "--conn");
    var batchId = Guid.Parse(Require(args, "--batch"));
    var sink = new PostgresSink(conn);
    var reverted = await sink.RollbackBatchAsync(batchId);
    Console.WriteLine($"rolled back batch {batchId}: {reverted} rows reverted (soft).");
    return 0;
}

static async Task<(PostgresSink Sink, MigrationBatch Batch, StreamConfig Config, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, FileAuditClient Audit)>
    Setup(string[] args, string stream)
{
    var conn = Require(args, "--conn");
    var env = Arg(args, "--env") ?? "staging";
    var masked = env != "production"; // lower envs must be masked; prod is the only unmasked env.
    if (!masked && Arg(args, "--i-understand-prod") is null)
        throw new ArgumentException("production runs require --i-understand-prod and a passed go-live gate (../35 §3).");

    var config = Arg(args, "--config") is { } cfgPath ? StreamConfig.FromJson(File.ReadAllText(cfgPath)) : DefaultConfigs.For(stream);
    var rows = ReadCsv(Require(args, "--csv"));
    var sink = new PostgresSink(conn);
    await sink.EnsureSchemaAsync();
    var batch = MigrationBatch.Start(config, env, DateTimeOffset.UtcNow, masked);
    await sink.RegisterBatchAsync(batch);
    var audit = new FileAuditClient(Arg(args, "--audit-log") ?? $"migration-audit-{batch.BatchId}.jsonl");
    Console.WriteLine($"batch {batch.BatchId} — stream={stream} env={env} masked={masked} config={config.Version}");
    return (sink, batch, config, rows, audit);
}

static void Guard(ReconciliationReport recon)
{
    if (recon.Balances) return;
    Console.Error.WriteLine($"reconciliation does NOT balance ({recon.Loaded + recon.Held + recon.Rejected} accounted of {recon.SourceCount}); triage exceptions before promotion.");
    foreach (var e in recon.Exceptions.Take(20)) Console.Error.WriteLine($"  - {e.SourceId}: {e.Reason}");
}

static IReadOnlyList<IReadOnlyDictionary<string, string?>> ReadCsv(string path)
{
    if (!File.Exists(path)) throw new FileNotFoundException($"source CSV not found: {path}");
    using var reader = new StreamReader(path);
    using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { DetectColumnCountChanges = false });
    csv.Read(); csv.ReadHeader();
    var headers = csv.HeaderRecord ?? throw new FormatException("CSV has no header row");
    var rows = new List<IReadOnlyDictionary<string, string?>>();
    while (csv.Read())
    {
        var row = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var h in headers) row[h] = csv.GetField(h);
        rows.Add(row);
    }
    return rows;
}

static string Require(string[] args, string name)
    => Arg(args, name) ?? throw new ArgumentException($"missing required argument {name}");

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        Mersal HBMP migration toolkit (phase 12.1)

        Usage:
          mersal-migrate default-config --stream <providers|beneficiaries>
          mersal-migrate run-providers      --conn <cs> --csv <file> [--config <json>] [--env staging] [--audit-log <file>]
          mersal-migrate run-beneficiaries  --conn <cs> --csv <file> [--config <json>] [--env staging] [--audit-log <file>]
          mersal-migrate rollback           --conn <cs> --batch <guid>

        Guardrails: lower-env runs are masked-only; production requires --i-understand-prod and a passed
        go-live gate. Every run is idempotent (upsert on natural key), reversible (rollback --batch), and
        audited. Reconciliation must balance and dedupe review-queue must be signed off before promotion.
        """);
}

namespace Mersal.Migration
{
    /// <summary>Marker so the test project can reference this Exe assembly's types.</summary>
    public static class MigrationCli;
}
