using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Authz.Tests;

/// <summary>
/// Phase 18.B3 (audit R2 S3/S6) — the policy bundles and the identity seed must agree.
///
/// Authorization on this platform is two tables that have to line up. A <see cref="PolicyRule"/> in code names
/// a role AND a scope, and the engine denies unless the principal holds both. The rules are reviewed as code;
/// the grants live in <c>identity/…/0001_identity.sql</c> as seed data and are not. When they disagree the
/// result is a permanent, silent 403: <c>missing-scope</c> in a log, for a role the design says is entitled.
///
/// Three such gaps existed before 18.B3 and none of them failed a test, because a denial IS the fail-safe
/// outcome — nothing crashes, nothing warns, the feature is just quietly unreachable:
///   • <c>medical_director</c> is named on EditMasterData (FR-MDM-008 puts the ICD/CPT/drug catalogue under
///     clinical governance), EditTemplate and ReadDashboard, and held neither admin:read nor admin:write.
///   • Seven roles are named as break-glass originators; only <c>super_admin</c> held admin:break-glass, so
///     the emergency PHI path was reachable by the one person not at the bedside.
///   • Reading the beneficiary directory required <c>patient:write</c>, which reception does not hold.
///
/// This test reads both sides and fails on any rule no named role can satisfy.
/// </summary>
public class ScopeIntegrityTests
{
    /// <summary>The staff-facing bundles. InteropPolicies is absent BY DESIGN: its <c>fhir:*</c> SMART-on-FHIR
    /// scopes are granted per external CLIENT under a data-sharing agreement, never to an internal staff role,
    /// so <c>role_scope</c> is the wrong table to look them up in (ADR-0016).</summary>
    private static readonly PolicyBundle[] Bundles =
    [
        AdminPolicies.Bundle(), PatientPolicies.Bundle(), ClaimsPolicies.Bundle(), EmrPolicies.Bundle(),
        OrdersPolicies.Bundle(), PharmacyPolicies.Bundle(), ApprovalsPolicies.Bundle(), CasePolicies.Bundle(),
        FinancePolicies.Bundle(), ProviderPolicies.Bundle(), ReportingPolicies.Bundle(),
        NotificationPolicies.Bundle(), DocumentPolicies.Bundle(), CallCentrePolicies.Bundle(),
    ];

    /// <summary>
    /// Roles named by a policy rule that do NOT exist in the frozen role vocabulary, with why each is still
    /// open. Every one is a real role in 10-role-matrix.md; adding it to the vocabulary changes the token
    /// contract AND the SPA's role→portal mapping, so it is a product decision rather than a seed fix. The
    /// consequence today is that the rule is satisfied only by the OTHER roles it names — which for
    /// <c>claims:settle</c> (finance, manager) and <c>claims:export</c> means the second approver in a
    /// dual-control pair may not exist yet. Tracked in docs/PHASE-18-TODO.md.
    /// </summary>
    private static readonly Dictionary<string, string> UndeclaredRoles = new(StringComparer.Ordinal)
    {
        ["claims_reviewer"] = "10-role-matrix §3.17 'Claims Officer / Claims Reviewer' — the senior half of the claims pair; claims_officer covers the rules today",
        ["manager"] = "an oversight/management tier named by claims + reporting rules; overlaps medical_director and org_admin and needs a decision on which it is",
        ["network_manager"] = "provider-network management; network_team exists and may be the intended name",
        ["approvals_team"] = "named by one approvals rule; medical_approval is the vocabulary role for the same people",
        ["finance_approver"] = "the second signature on a finance approval; finance holds finance:approve today, so dual control rests on the handler's SoD check alone",
        ["call_center_supervisor"] = "15.6 supervisor KPI surface; call_center covers the agent rules",
    };

    [Fact]
    public void Every_role_named_on_a_rule_can_actually_hold_one_of_the_scopes_it_requires()
    {
        var seed = RoleScopes();
        var unreachable = new List<string>();

        foreach (var rule in Bundles.SelectMany(b => b.Rules).DistinctBy(r => $"{r.Action}|{r.ResourceType}"))
        {
            if (rule.Roles.Count == 0 || rule.Scopes.Count == 0) continue;   // unconstrained on one axis
            foreach (var role in rule.Roles.Order(StringComparer.Ordinal))
            {
                if (UndeclaredRoles.ContainsKey(role)) continue;   // declared exception, asserted below
                var held = seed.TryGetValue(role, out var scopes) ? scopes : [];
                if (rule.Scopes.Any(held.Contains)) continue;
                unreachable.Add(
                    $"{rule.Action} on {rule.ResourceType}: role '{role}' is named by the rule but holds none of " +
                    $"[{string.Join(", ", rule.Scopes.Order(StringComparer.Ordinal))}] in the identity seed");
            }
        }

        unreachable.Should().BeEmpty(
            "a rule that names a role which cannot hold its scope is a feature nobody can reach and no test " +
            "reports:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, unreachable));
    }

    [Fact]
    public void Every_scope_a_rule_requires_exists_in_the_catalog()
    {
        // A typo in a rule's scope name is default-deny too, and reads as a deliberate restriction.
        var catalog = CatalogScopes();
        foreach (var rule in Bundles.SelectMany(b => b.Rules))
            foreach (var scope in rule.Scopes)
                catalog.Should().Contain(scope,
                    "{0} on {1} requires '{2}', which is not in the frozen scope vocabulary",
                    rule.Action, rule.ResourceType, scope);
    }

    [Fact]
    public void Every_granted_scope_exists_in_the_catalog()
    {
        // The FK on role_scope enforces this in the database; asserting it here catches a bad seed edit before
        // it reaches a migration run.
        var catalog = CatalogScopes();
        foreach (var (role, scopes) in RoleScopes())
            foreach (var scope in scopes)
                catalog.Should().Contain(scope, "role '{0}' is granted '{1}', which is not a catalog scope", role, scope);
    }

    [Fact]
    public void The_undeclared_role_list_does_not_go_stale()
    {
        // Two directions. A role that HAS been added to the vocabulary must leave this list, or it silently
        // keeps excusing itself. And a role must still be referenced by some rule — an entry for a role no
        // rule names is dead weight that makes the register look worse than it is.
        var seedRoles = SeedRoles();
        var namedByRules = Bundles.SelectMany(b => b.Rules).SelectMany(r => r.Roles).ToHashSet(StringComparer.Ordinal);

        foreach (var (role, reason) in UndeclaredRoles)
        {
            seedRoles.Should().NotContain(role,
                "'{0}' now exists in the vocabulary — remove it from UndeclaredRoles so the check applies ({1})", role, reason);
            namedByRules.Should().Contain(role,
                "'{0}' is listed as an undeclared role but no policy rule names it any more", role);
        }
    }

    [Fact]
    public void The_scan_reads_a_plausible_seed()
    {
        // Guards the guard: an empty parse would make all three tests above vacuously green.
        RoleScopes().Should().HaveCountGreaterThan(10, "the seed grants scopes to every role in the catalog");
        CatalogScopes().Should().HaveCountGreaterThan(40);
    }

    // ---- the seed, read straight out of the migration ---------------------------------------------------

    private static Dictionary<string, HashSet<string>> RoleScopes()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Grant(string role, string scope)
        {
            if (!map.TryGetValue(role, out var set)) map[role] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(scope);
        }

        // The seed grants roles in three shapes across the migrations; each statement body is isolated first so
        // a tuple from an unrelated INSERT (the scope catalogue, the role tiers) cannot be misread as a grant.
        foreach (var body in Statements("INSERT INTO identity.role_scope"))
        {
            // (a) `VALUES ('role','scope'), …` — pairs, with or without a space after the comma.
            foreach (Match m in Regex.Matches(body, @"\('([a-z_]+)',\s*'([a-z:_\-]+)'\)"))
                Grant(m.Groups[1].Value, m.Groups[2].Value);

            // (b) `SELECT role, '<scope>' FROM (VALUES ('a'),('b')) AS r(role)` — one scope, many roles.
            foreach (Match m in Regex.Matches(body, @"SELECT role,\s*'([a-z:_\-]+)'\s*FROM \(VALUES(.*?)\) AS \w+\(role\)", RegexOptions.Singleline))
                foreach (Match r in Regex.Matches(m.Groups[2].Value, @"\('([a-z_]+)'\)"))
                    Grant(r.Groups[1].Value, m.Groups[1].Value);

            // (c) `SELECT '<role>', scope FROM (VALUES ('a'),('b')) AS s(scope)` — one role, many scopes.
            foreach (Match m in Regex.Matches(body, @"SELECT '([a-z_]+)',\s*scope\s*FROM \(VALUES(.*?)\) AS \w+\(scope\)", RegexOptions.Singleline))
                foreach (Match sc in Regex.Matches(m.Groups[2].Value, @"\('([a-z:_\-]+)'\)"))
                    Grant(m.Groups[1].Value, sc.Groups[1].Value);
        }
        return map;
    }

    private static HashSet<string> CatalogScopes()
    {
        var catalog = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in Statements("INSERT INTO identity.scope"))
            foreach (Match m in Regex.Matches(body, @"\('([a-z:_\-]+)',\s*'[a-z\-]+',\s*(?:true|false)\)"))
                catalog.Add(m.Groups[1].Value);
        return catalog;
    }

    /// <summary>The text of every statement in the seed beginning with <paramref name="prefix"/>, up to its
    /// terminating semicolon. Isolating statements is what lets the shape-specific patterns above stay simple
    /// without matching tuples from a neighbouring INSERT.</summary>
    private static IEnumerable<string> Statements(string prefix)
    {
        var sql = SeedSql();
        var at = 0;
        while ((at = sql.IndexOf(prefix, at, StringComparison.Ordinal)) >= 0)
        {
            var end = sql.IndexOf(';', at);
            if (end < 0) end = sql.Length - 1;
            yield return sql[at..end];
            at = end;
        }
    }

    /// <summary>Roles that exist in the frozen vocabulary (they carry a sensitivity tier in the seed).</summary>
    private static HashSet<string> SeedRoles() =>
        new(Regex.Matches(SeedSql(), @"\('([a-z_]+)','T\d'\)").Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static string? _sql;

    /// <summary>Every identity migration, in apply order, with <c>--</c> comments stripped. Stripping matters:
    /// statements are split on ';' and prose comments contain semicolons, which would silently truncate a
    /// statement mid-VALUES and drop the grants after it — a parser bug that reads exactly like a real gap.</summary>
    private static string SeedSql() => _sql ??= string.Join('\n',
        Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "services", "identity", "Infrastructure", "Migrations"), "*.sql")
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .Select(line =>
            {
                var at = line.IndexOf("--", StringComparison.Ordinal);
                return at < 0 ? line : line[..at];
            }));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
