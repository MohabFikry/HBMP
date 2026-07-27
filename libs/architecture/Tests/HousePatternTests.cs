using System.Text.RegularExpressions;
using FluentAssertions;
using NetArchTest.Rules;

namespace Mersal.Architecture.Tests;

/// <summary>
/// Phase 18.E2 (audit R2) — the house pattern, enforced at build time.
///
/// The R2 audit's own assessment is the reason this project exists: rules of this shape would have caught
/// FOUR of the six criticals before review. Not because the code was careless, but because every one of
/// those defects was an OMISSION — a service that did not call a binder, a policy that did not exist, a
/// connection string nobody flipped. An omission cannot fail a unit test, because the test that would have
/// caught it is the test nobody wrote either. It can only be caught by something that asserts a pattern
/// holds EVERYWHERE, including in the file that does not exist yet.
///
/// Which criticals:
///   X6/S1/S2 — a service whose Program.cs never called UseHbmpRls, and one whose policy was fail-open.
///   S7       — the one service missing UseHbmpTransportSecurity, and it was the token issuer.
///   X9/X10   — bare UtcNow on business-date paths (covered by libs/time's own scanner since 18.A3).
///
/// Two styles are used deliberately. NetArchTest for the LAYERING rule, because that is a question about
/// compiled assembly references and it answers it exactly. Source scanning for the wiring rules, because
/// "does Program.cs call this middleware" is not visible in IL at all — the thing being asserted is the
/// presence of a call in a top-level statement file, and reading the file is the honest way to check.
/// </summary>
public class HousePatternTests
{
    // ---------------------------------------------------------------- layering

    [Fact]
    public void Domain_never_references_Infrastructure()
    {
        // Domain is the layer that must stay testable without a database, a broker, or a clock. A reference
        // the other way is how a "pure" rule quietly acquires a DbContext and stops being unit-testable —
        // and it is invisible until someone tries to write the test.
        var domains = new[]
        {
            typeof(Mersal.Claims.Domain.ClaimDecision).Assembly,
            typeof(Mersal.Orders.Domain.ReportAccessWorkflow).Assembly,
            typeof(Mersal.Emr.Domain.PractitionerBranchRules).Assembly,
            typeof(Mersal.Policy.Domain.LimitReset).Assembly,
            typeof(Mersal.Patient.Domain.BeneficiaryLifecycle).Assembly,
        };

        foreach (var asm in domains)
        {
            var result = Types.InAssembly(asm)
                .ShouldNot()
                .HaveDependencyOnAny("Mersal.Claims.Infrastructure", "Mersal.Orders.Infrastructure",
                    "Mersal.Emr.Infrastructure", "Mersal.Policy.Infrastructure", "Mersal.Patient.Infrastructure",
                    "Microsoft.EntityFrameworkCore", "Npgsql", "RabbitMQ.Client")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                "{0} is a Domain assembly and must not depend on Infrastructure, EF Core, Npgsql or the broker — " +
                "offenders: {1}", asm.GetName().Name,
                string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    // ---------------------------------------------------------------- wiring

    /// <summary>Services that legitimately do not bind the RLS tenant GUC, each with its reason. The list is
    /// asserted for staleness below, so it cannot quietly absorb a service that simply forgot.</summary>
    private static readonly Dictionary<string, string> RlsExempt = new(StringComparer.Ordinal)
    {
        ["masterdata"] = "reference catalogue (ICD/CPT/LOINC/ATC) — tenant-FREE by design: a diagnosis code means the same thing for every tenant (18.B2)",
        ["audit"] = "RLS keys on ROLE MEMBERSHIP (pg_has_role), not a tenant GUC — it runs as hbmp_audit, not hbmp_app (18.B2)",
        ["identity"] = "the issuer looks a user up BY USERNAME to discover their tenant, before any request-scoped tenant exists — tenant RLS here would break login (identity 0002)",
    };

    [Fact]
    public void Every_service_binds_the_rls_tenant_guc()
    {
        // X6/S1/S2. claims, callcentre, admin and interop each had correct RLS policies and no binder, so the
        // policies never evaluated — and the day someone flipped the connection string off the superuser,
        // every query would have returned zero rows instead.
        var missing = ProgramFiles()
            .Where(p => !RlsExempt.ContainsKey(p.Service))
            .Where(p => !p.Source.Contains("UseHbmpRls(", StringComparison.Ordinal))
            .Select(p => p.Service).ToList();

        missing.Should().BeEmpty(
            "these services persist tenant-scoped rows and never bind app.tenant_id, so their RLS policies " +
            "cannot evaluate: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void Every_service_enforces_transport_security()
    {
        // S7. identity-service was the ONLY service missing this — and it is the one that carries passwords,
        // TOTP codes and bearer tokens.
        var missing = ProgramFiles()
            .Where(p => !p.Source.Contains("UseHbmpTransportSecurity(", StringComparison.Ordinal))
            .Select(p => p.Service).ToList();

        missing.Should().BeEmpty("these services serve traffic without HSTS/HTTPS enforcement: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void The_rls_exemption_list_does_not_go_stale()
    {
        // An exemption for a service that no longer exists silently excuses the next one added under that
        // name; an exemption for a service that HAS since added the binder is dead weight that makes the
        // register look worse than it is.
        var services = ProgramFiles().Select(p => p.Service).ToHashSet(StringComparer.Ordinal);
        foreach (var (svc, reason) in RlsExempt)
        {
            services.Should().Contain(svc, "'{0}' is exempted ({1}) but has no Program.cs", svc, reason);
            var src = ProgramFiles().First(p => p.Service == svc).Source;
            src.Should().NotContain("UseHbmpRls(",
                "'{0}' now binds the GUC — remove it from RlsExempt so the rule applies", svc);
        }
    }

    // ---------------------------------------------------------------- data

    [Fact]
    public void Every_tenant_scoped_table_has_an_rls_policy()
    {
        // X6, generalised. A table carrying tenant_id and no policy is isolated only by whatever WHERE clause
        // the application remembers — which is precisely the state callcentre shipped in for three phases.
        var offenders = new List<string>();

        foreach (var migDir in Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "services"))
                     .Select(d => Path.Combine(d, "Infrastructure", "Migrations"))
                     .Where(Directory.Exists))
        {
            var service = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(migDir))!)!;
            if (RlsExempt.ContainsKey(service)) continue;

            var sql = string.Concat(Directory.EnumerateFiles(migDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal)
                .Select(File.ReadAllText));

            foreach (Match table in Regex.Matches(sql,
                         @"CREATE TABLE IF NOT EXISTS ""?(\w+)""?\.(\w+)\s*\((.*?)\n\);", RegexOptions.Singleline))
            {
                var name = table.Groups[2].Value;
                if (!Regex.IsMatch(table.Groups[3].Value, @"^\s*tenant_id\b", RegexOptions.Multiline)) continue;
                if (LedgerTables.Contains(name)) continue;
                if (!HasPolicy(sql, name)) offenders.Add($"{service}.{name}");
            }
        }

        offenders.Should().BeEmpty(
            "these tables carry tenant_id with NO row-level policy — isolation rests entirely on the " +
            "application predicate:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Tables that carry a tenant column and are deliberately RLS-free, each with its reason. Every
    /// entry is a decision, not an oversight — and the staleness test below fails if one stops existing.</summary>
    private static readonly Dictionary<string, string> RlsFreeTables = new(StringComparer.Ordinal)
    {
        ["processed_event"] = "consumer dedupe ledger — read on the replay path BEFORE the request's tenant is resolved",
        ["processed_request"] = "idempotency ledger — same: the key is looked up before a tenant exists",
        ["outbox_message"] = "relay ledger, drained by a background publisher with no request principal",
        ["tenant"] = "admin.tenant is the tenant REGISTRY itself — isolating it on its own primary key would reduce a Super Admin's platform-wide list to a single row (admin 0005)",
    };

    private static HashSet<string> LedgerTables => [.. RlsFreeTables.Keys];

    [Fact]
    public void Money_is_never_a_bare_double_or_float()
    {
        // A binary float cannot represent 0.10, so a sum of currency drifts. The platform uses decimal
        // everywhere already; this stops the first exception from being introduced quietly.
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file),
                         @"public\s+(?:double|float)\??\s+(\w*(?:Amount|Price|Total|Cost|Value|Spend|Payable|Balance|Fee)\w*)\s*\{"))
                offenders.Add($"{Relative(file)}: {m.Groups[1].Value}");
        }

        offenders.Should().BeEmpty(
            "money must be decimal — binary floating point cannot represent 0.10 and a sum of currency " +
            "drifts:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }


    [Fact]
    public void The_rls_free_table_list_does_not_go_stale()
    {
        // Same discipline as the service exemptions: a named exception must still name something real, or it
        // silently excuses whatever is added under that name next.
        var allTables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var migDir in Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "services"))
                     .Select(d => Path.Combine(d, "Infrastructure", "Migrations")).Where(Directory.Exists))
            foreach (Match m in Regex.Matches(
                         string.Concat(Directory.EnumerateFiles(migDir, "*.sql").Select(File.ReadAllText)),
                         @"CREATE TABLE IF NOT EXISTS ""?\w+""?\.(\w+)"))
                allTables.Add(m.Groups[1].Value);

        foreach (var (table, reason) in RlsFreeTables)
            allTables.Should().Contain(table, "'{0}' is declared RLS-free ({1}) but no such table exists", table, reason);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record ProgramFile(string Service, string Source);

    private static ProgramFile[]? _programs;
    private static ProgramFile[] ProgramFiles() => _programs ??=
    [
        .. Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "services"))
            .Select(d => (Service: Path.GetFileName(d)!, Path: Path.Combine(d, "Api", "Program.cs")))
            .Where(x => File.Exists(x.Path))
            .Select(x => new ProgramFile(x.Service, File.ReadAllText(x.Path)))
    ];

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "services"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "libs"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string p) => Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/');


    /// <summary>
    /// Does a policy exist for this table? Two shapes, because the migrations use both:
    ///   * literal      — CREATE POLICY rls_x ON schema.x ...
    ///   * DYNAMIC      — FOREACH t IN ARRAY ARRAY['a','b'] ... EXECUTE format('CREATE POLICY rls_%1$s ...')
    /// The dynamic form is the one most services use (it applies one policy shape to a list of tables), and
    /// a detector that only saw the literal form would report every one of them as unprotected — a false
    /// alarm loud enough that the rule would be deleted rather than fixed.
    /// </summary>
    private static bool HasPolicy(string sql, string table)
    {
        if (Regex.IsMatch(sql, $@"CREATE POLICY \w+ ON \w+\.{Regex.Escape(table)}\b")) return true;

        // Dynamic: the table name appears inside an ARRAY[...] whose loop body creates a policy.
        foreach (Match block in Regex.Matches(sql, @"ARRAY\[([^\]]*)\](.*?)END \$\$;", RegexOptions.Singleline))
        {
            var names = Regex.Matches(block.Groups[1].Value, @"'([\w]+)'").Select(m => m.Groups[1].Value);
            if (names.Contains(table, StringComparer.Ordinal)
                && block.Groups[2].Value.Contains("CREATE POLICY", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
