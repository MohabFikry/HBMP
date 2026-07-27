using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Data.Tests;

/// <summary>
/// Phase 18.B2 (audit R2 X6, S1, S2) — the gate that makes the RLS work actually stick.
///
/// Row-level security needs three things simultaneously and gives no signal when one is missing:
///   1. a fail-CLOSED policy on the table,
///   2. a service that BINDS <c>app.tenant_id</c> per request, and
///   3. a runtime connection under a NOBYPASSRLS role.
/// Miss (3) and the correct policies never evaluate — that was provider-service, whose green isolation test
/// proved the policy while the deployment bypassed it. Miss (2) and every query returns zero rows the moment
/// someone fixes (3) — the "deny-all trap" in claims and admin. Miss (1) and the whole thing is theatre,
/// which is what interop's `OR current_setting(...) IS NULL` amounted to.
///
/// None of those is visible to the compiler and only (1)+(2) are visible to a test that talks to a database.
/// This suite reads the deployment and the migrations directly, so a regression is a red build rather than a
/// finding in the next audit.
/// </summary>
public class RlsDeploymentPostureTests
{
    [Fact]
    public void No_runtime_connection_string_uses_the_postgres_superuser()
    {
        var offenders = ConnectionStrings()
            .Where(c => c.Value.Contains("POSTGRES_USER", StringComparison.Ordinal))
            .Select(c => $"compose.yaml:{c.Line} {c.Key} connects as ${{POSTGRES_USER}}")
            .ToList();

        offenders.Should().BeEmpty(
            "a superuser BYPASSES row-level security entirely, so every policy in the schema it reaches is " +
            "inert no matter how correct it is:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Every_service_connects_as_a_known_least_privilege_role()
    {
        // Named roles rather than "not the superuser", so inventing a third role is a deliberate act with a
        // test change attached. hbmp_audit exists because audit's RLS keys on ROLE MEMBERSHIP: putting
        // audit-service on the shared hbmp_app would hand the whole fleet read of the audit trail.
        string[] permitted = ["hbmp_app", "hbmp_audit"];

        foreach (var c in ConnectionStrings())
        {
            var user = Regex.Match(c.Value, @"Username=([^;""]+)").Groups[1].Value;
            permitted.Should().Contain(user,
                "compose.yaml:{0} {1} connects as '{2}', which is not a recognised runtime role", c.Line, c.Key, user);
        }
    }

    [Fact]
    public void The_compose_stack_declares_a_password_for_every_runtime_role_it_uses()
    {
        // A missing HBMP_*_PASSWORD is a stack that will not start, discovered at deploy time. Cheap to catch here.
        var example = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "compose", ".env.example"));
        foreach (var variable in ConnectionStrings()
                     .SelectMany(c => Regex.Matches(c.Value, @"\$\{(HBMP_[A-Z_]*PASSWORD)\}").Select(m => m.Groups[1].Value))
                     .Distinct())
            example.Should().Contain(variable, "{0} is referenced by a connection string but undocumented in .env.example", variable);
    }

    [Fact]
    public void No_migration_introduces_a_fail_open_tenant_policy()
    {
        // The shape: `tenant_id = current_setting(...) OR current_setting(...) IS NULL`. The second disjunct is
        // true on every connection that has not bound the GUC, which — for a background worker, a maintenance
        // session, or a service that simply forgot the binder — is all of them. It reads as isolation and
        // enforces nothing, and it spread by imitation: interop 0001 cites admin 0001 as its precedent.
        var offenders = new List<string>();
        foreach (var file in MigrationFiles())
        {
            var name = Path.GetFileName(file);
            if (Superseded.Contains(name)) continue;
            // Strip `--` comments first: the migrations that CLOSED this hole quote the offending shape to
            // explain it, and a scan that cannot tell an explanation from a policy would forbid documenting it.
            var sql = string.Join('\n', File.ReadAllLines(file)
                .Select(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) ? "" : l));
            if (Regex.IsMatch(sql, @"current_setting\('app\.\w+',\s*true\)\s*IS NULL", RegexOptions.IgnoreCase))
                offenders.Add(Relative(file));
        }

        offenders.Should().BeEmpty(
            "a policy that permits everything when the GUC is unset is worse than no policy, because it looks " +
            "like one in review:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_superseded_list_does_not_go_stale()
    {
        // If someone rewrites history or renames a migration, the allowlist must shrink with it — otherwise it
        // silently starts excusing a file that no longer exists while a new offender hides behind the name.
        var present = MigrationFiles().Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        foreach (var name in Superseded)
            present.Should().Contain(name, "{0} is allow-listed as superseded but no longer exists", name);

        foreach (var name in Superseded)
        {
            var file = MigrationFiles().First(f => Path.GetFileName(f) == name);
            var replacement = Path.Combine(Path.GetDirectoryName(file)!, SupersededBy[name]);
            File.Exists(replacement).Should().BeTrue(
                "{0} is excused only because {1} drops and recreates its policies fail-closed", name, SupersededBy[name]);
        }
    }

    /// <summary>Historic migrations that shipped the fail-open shape. Migrations are append-only, so these
    /// files stay exactly as they were applied; a later migration in the same schema drops and recreates every
    /// policy they created. Each entry names the migration that supersedes it, and the test above fails if
    /// that migration disappears.</summary>
    private static readonly Dictionary<string, string> SupersededBy = new(StringComparer.Ordinal)
    {
        ["0001_admin.sql"] = "0005_tenant_rls_fail_closed.sql",
        ["0002_admin_governance.sql"] = "0005_tenant_rls_fail_closed.sql",
        ["0003_admin_breakglass_tenant.sql"] = "0005_tenant_rls_fail_closed.sql",
        ["0004_user_branch_assignment.sql"] = "0005_tenant_rls_fail_closed.sql",
        ["0001_interop.sql"] = "0003_tenant_rls.sql",
    };

    private static HashSet<string> Superseded => [.. SupersededBy.Keys];

    private sealed record ConnectionString(int Line, string Key, string Value);

    private static IEnumerable<ConnectionString> ConnectionStrings()
    {
        var path = Path.Combine(RepoRoot(), "infra", "compose", "compose.yaml");
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"(ConnectionStrings__\w+):\s*""([^""]+)""");
            if (m.Success) yield return new ConnectionString(i + 1, m.Groups[1].Value, m.Groups[2].Value);
        }
    }

    private static IEnumerable<string> MigrationFiles() =>
        Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "services"))
            .Select(s => Path.Combine(s, "Infrastructure", "Migrations"))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.sql"));

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
