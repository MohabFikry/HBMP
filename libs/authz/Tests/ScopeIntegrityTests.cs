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
        PolicyPolicies.Bundle(), ProfilePolicies.Bundle(),
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
        // 19.7 REMOVED policy_admin and beneficiary_mgmt_supervisor from this list: both now exist in the
        // frozen vocabulary with a seeded scope set, so the reachability check above applies to them for
        // real. That is the point of the staleness assertion below — an exception that outlives its reason
        // is a check quietly switched off.
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

    /*
     * ---------------------------------------------------------------------------------------------------
     * A PARSER THAT CANNOT READ A MIGRATION MUST SAY SO, NOT READ IT AS EMPTY.
     *
     * Everything below scrapes the identity seed with regexes, and the failure mode of a scraper is not a
     * crash — it is quietly returning less. `Every_seed_statement_is_one_this_parser_understands` closes
     * that: a scope or grant statement no pattern here matches fails the build naming the migration, rather
     * than removing a scope from the catalogue and reporting the FEATURE as broken.
     *
     * That is not hypothetical. Migration 0027 (`auth:configure`) writes the catalogue row with the full
     * six-column list and grants it with a tenant fan-out; neither shape existed when these patterns were
     * written. The result was two red tests blaming ApprovalsPolicies for requiring a scope "not in the
     * frozen vocabulary" — a scope that was in fact seeded correctly, in a form the reader was blind to.
     * ---------------------------------------------------------------------------------------------------
     */

    [Fact]
    public void Every_seed_statement_is_one_this_parser_understands()
    {
        var unreadable = new List<string>();

        foreach (var (file, body) in Statements("INSERT INTO identity.scope"))
            if (ScopesIn(body).Count == 0) unreadable.Add($"{file} — INSERT INTO identity.scope yielded no scope");

        // The REAL accumulated map as the "already granted" set: this test asks only whether a statement is
        // READABLE, and both derive-from-existing-grants shapes — (g) fan-out and (h) role copy — yield
        // nothing against an empty one for a legitimate reason. 29.1 changed this from SeedRoles(): shape (h)
        // copies a source role's SCOPES, so a set of role names alone reads a rename as unreadable.
        var accumulated = RoleScopes();
        foreach (var (file, body) in Statements("INSERT INTO identity.role_scope"))
            if (!IsRedistribution(body) && GrantsIn(body, accumulated).Count == 0)
                unreadable.Add($"{file} — INSERT INTO identity.role_scope yielded no grant");

        unreadable.Should().BeEmpty(
            "a seed statement this parser cannot read is one whose scopes silently vanish from the catalogue, "
            + "which then reports the RULE that requires them as the defect. Teach the shape to ScopesIn / "
            + "GrantsIn below rather than reshaping the migration:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, unreadable));
    }

    private static Dictionary<string, HashSet<string>> RoleScopes()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // In apply order, and the accumulated role set is fed forward: shape (g) grants a new scope to every
        // role that ALREADY holds something, so reading it faithfully means knowing what has been granted by
        // the time that migration runs.
        foreach (var (_, body) in Statements("INSERT INTO identity.role_scope"))
            foreach (var (role, scope) in GrantsIn(body, map))
            {
                if (!map.TryGetValue(role, out var set)) map[role] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(scope);
            }
        return map;
    }

    /// <summary>
    /// Every (role, scope) grant in ONE isolated <c>role_scope</c> statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seed grants in seven shapes. Each statement body is isolated first, so a tuple from a neighbouring
    /// INSERT (the scope catalogue, the role tiers) cannot be misread as a grant.
    /// </para>
    /// <para>
    /// An unrecognised shape does not fail HERE — it reads as zero grants, so the reachability check would
    /// pass VACUOUSLY for every role granted that way. That is what
    /// <see cref="Every_seed_statement_is_one_this_parser_understands"/> is for, and it is not hypothetical:
    /// it found eight migrations this reader was blind to on the day it was written.
    /// </para>
    /// </remarks>
    /// <param name="grantedSoFar">
    /// Every (role, scope) granted by the time this statement runs. Its KEYS are the population shape (g)
    /// grants to; its VALUES are what shape (h) copies from.
    /// </param>
    private static HashSet<(string Role, string Scope)> GrantsIn(
        string body, IReadOnlyDictionary<string, HashSet<string>> grantedSoFar)
    {
        var rolesGrantedSoFar = grantedSoFar.Keys.ToHashSet(StringComparer.Ordinal);
        var grants = new HashSet<(string, string)>();

        // (h) 29.1 — ROLE COPY, the shape a RENAME has:
        //     `SELECT rs.tenant_id, '<new_role>', rs.scope_name FROM identity.role_scope rs
        //      WHERE rs.role_name = '<old_role>'`
        //
        // The new role inherits EVERY scope the old one holds, whatever that turns out to be. It is checked
        // before IsRedistribution because it names no scope literal — a rename cannot, since enumerating the
        // scopes is precisely what it must not do. 0031 says why: `imaging_tech` accumulated six scopes across
        // five migrations, and a hand-written list in the copy would freeze today's set and silently omit
        // tomorrow's.
        //
        // Read as "whatever the source role holds by this point in apply order", which is exactly what the
        // migration does at runtime. Without this shape the copy reads as ZERO grants and the new role looks
        // unreachable to Every_role_named_on_a_rule_can_actually_hold_one_of_the_scopes_it_requires — which is
        // how this shape was found.
        var roleCopy = Regex.Match(
            body,
            @"SELECT\s+\w+\.tenant_id,\s*'(?<target>[a-z_]+)',\s*\w+\.scope_name\s+FROM\s+identity\.role_scope"
            + @"\s+\w+\s+WHERE\s+\w+\.role_name\s*=\s*'(?<source>[a-z_]+)'",
            RegexOptions.Singleline);
        if (roleCopy.Success)
        {
            var source = roleCopy.Groups["source"].Value;
            var target = roleCopy.Groups["target"].Value;
            if (grantedSoFar.TryGetValue(source, out var sourceScopes))
            {
                foreach (var scope in sourceScopes) grants.Add((target, scope));
            }
            return grants;
        }

        if (IsRedistribution(body)) return grants;

        // (a) `VALUES ('role','scope'), …` — pairs, with or without a space after the comma.
        foreach (Match m in Regex.Matches(body, @"\('([a-z_]+)',\s*'([a-z:_\-]+)'\)"))
            grants.Add((m.Groups[1].Value, m.Groups[2].Value));

        // (b) `SELECT role, '<scope>' FROM (VALUES ('a'),('b')) AS r(role)` — one scope, many roles.
        // (e) The same idea with a tenant fan-out in front of it:
        //     `SELECT DISTINCT rs.tenant_id, r.role_name, '<scope>' FROM (VALUES ('a'),('b')) AS r(role_name)
        //      CROSS JOIN …`.
        // role_scope is tenant-scoped and the platform-default row does NOT stand in for a tenant that has
        // its own grants, so newer migrations fan the grant across every tenant present. One pattern covers
        // both, and the alias is `role` or `role_name` depending on the migration's vintage.
        foreach (Match m in Regex.Matches(
            body,
            @"SELECT\b[^;]*?'(?<scope>[a-z]+:[a-z:_\-]+)'[^;]*?\(VALUES(?<roles>.*?)\)\s*AS \w+\((?:role|role_name)\)",
            RegexOptions.Singleline))
            foreach (Match r in Regex.Matches(m.Groups["roles"].Value, @"\('([a-z_]+)'\)"))
                grants.Add((r.Groups[1].Value, m.Groups["scope"].Value));

        // (i) 29.2b — ONE role literal, MANY scopes from a VALUES list, with a TENANT FAN-OUT in front:
        //     `SELECT DISTINCT rs.tenant_id, '<role>', s.scope
        //      FROM (VALUES ('s1'),('s2')) AS s(scope) CROSS JOIN (SELECT DISTINCT tenant_id FROM …) rs`
        //
        // Shape (c) is the same idea without the tenant fan-out and cannot match, because the projection now
        // leads with `rs.tenant_id`; shape (e) is the mirror image (one SCOPE literal, many roles). role_scope
        // is tenant-scoped and the platform-default row does not stand in for a tenant with its own grants, so
        // every new role has to fan out this way — which makes this the shape the NEXT new role will use too.
        foreach (Match m in Regex.Matches(
            body,
            @"SELECT\s+(?:DISTINCT\s+)?\w+\.tenant_id,\s*'(?<role>[a-z_]+)',\s*\w+\.scope\b[^;]*?"
            + @"\(VALUES(?<scopes>.*?)\)\s*AS \w+\(scope\)",
            RegexOptions.Singleline))
            foreach (Match sc in Regex.Matches(m.Groups["scopes"].Value, @"\('([a-z:_\-]+)'\)"))
                grants.Add((m.Groups["role"].Value, sc.Groups[1].Value));

        // (c) `SELECT '<role>', scope FROM (VALUES ('a'),('b')) AS s(scope)` — one role, many scopes.
        foreach (Match m in Regex.Matches(body, @"SELECT '([a-z_]+)',\s*scope\s*FROM \(VALUES(.*?)\) AS \w+\(scope\)", RegexOptions.Singleline))
            foreach (Match sc in Regex.Matches(m.Groups[2].Value, @"\('([a-z:_\-]+)'\)"))
                grants.Add((m.Groups[1].Value, sc.Groups[1].Value));

        // (d) 25.1 — `… (VALUES ('r1'),('r2')) AS r(role) CROSS JOIN (VALUES ('s1'),…) AS s(scope)`:
        // MANY roles × MANY scopes from ONE list. Design 42 §1 requires branch_coordinator and
        // clinics_manager to hold an identical set, and the cheapest way to guarantee that in the seed is
        // one scope list that cannot disagree with itself.
        foreach (Match m in Regex.Matches(
            body,
            @"\(VALUES(?<roles>.*?)\) AS \w+\((?:role|role_name)\)\s*CROSS JOIN \(VALUES(?<scopes>.*?)\) AS \w+\(scope\)",
            RegexOptions.Singleline))
            foreach (Match r in Regex.Matches(m.Groups["roles"].Value, @"\('([a-z_]+)'\)"))
                foreach (Match sc in Regex.Matches(m.Groups["scopes"].Value, @"\('([a-z:_\-]+)'\)"))
                    grants.Add((r.Groups[1].Value, sc.Groups[1].Value));

        // (f) and (g) read the PROJECTION LIST ONLY — the text between SELECT and its FROM. A derived grant
        // routinely names in its WHERE clause the scope it is derived FROM
        // ("… WHERE scope_name = 'appointment:read'"), and that is a filter, not a grant. Scanning the whole
        // statement would hand call_center a grant of the very scope the migration was reading to find it.
        var projection = Regex.Match(body, @"SELECT\s+(?:DISTINCT\s+)?(?<cols>.*?)\s+FROM\b", RegexOptions.Singleline);
        if (projection.Success)
        {
            var cols = projection.Groups["cols"].Value;
            var scope = Regex.Match(cols, @"'(?<s>[a-z]+:[a-z:_\-]+)'");
            if (scope.Success)
            {
                // A role-shaped literal has no colon; the scope literal above cannot be mistaken for one.
                var roleLiteral = Regex.Match(cols, @"'(?<r>[a-z_]+)'");
                var derivesRoles = Regex.IsMatch(cols, @"(?:^|[\s,.])role_name\b");
                var hasInlineRoleList = Regex.IsMatch(
                    body, @"\(VALUES.*?\)\s*AS \w+\((?:role|role_name)\)", RegexOptions.Singleline);

                if (roleLiteral.Success)
                {
                    // (f) `SELECT 'call_center', 'appointment:reserve', tenant_id FROM identity.role_scope
                    //      WHERE …` — one named role, one named scope, projected over existing rows so the
                    //      grant lands once per tenant.
                    grants.Add((roleLiteral.Groups["r"].Value, scope.Groups["s"].Value));
                }
                else if (derivesRoles && !hasInlineRoleList)
                {
                    // (g) `SELECT DISTINCT role_name, '<scope>' FROM identity.role_scope` — EVERY role that
                    //     already holds something gains the scope. 0022 grants masterdata:read this way, and
                    //     deliberately: a diagnosis code means the same thing to every clinical role, so the
                    //     catalogue restricts nobody and the grant is written to say exactly that.
                    foreach (var role in rolesGrantedSoFar) grants.Add((role, scope.Groups["s"].Value));
                }
            }
        }

        return grants;
    }

    /// <summary>
    /// Whether a <c>role_scope</c> statement redistributes existing grants rather than creating one.
    /// </summary>
    /// <remarks>
    /// A statement naming no scope literal anywhere cannot introduce a new (role, scope) pair — it can only
    /// be copying rows that already exist. <c>0012_provision_tenant_role_scopes</c> replicates the
    /// platform-default set into every tenant, which is exactly that. Yielding no grant is the CORRECT
    /// reading of it, so it is excluded from the unreadable-statement check rather than being taught a shape
    /// it does not have.
    /// </remarks>
    private static bool IsRedistribution(string body) => !Regex.IsMatch(body, @"'[a-z]+:[a-z:_\-]+'");

    private static HashSet<string> CatalogScopes()
    {
        var catalog = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, body) in Statements("INSERT INTO identity.scope"))
            catalog.UnionWith(ScopesIn(body));
        return catalog;
    }

    /// <summary>
    /// Every scope name declared by ONE isolated <c>identity.scope</c> statement.
    /// </summary>
    /// <remarks>
    /// Keyed on the tuple's FIRST literal rather than on a fixed column count. The catalogue is written both
    /// as the short <c>(name, domain, service_only)</c> and — since the description, deprecation and
    /// platform-admin-key columns arrived — as the full six-column form with a multi-line description. A
    /// pattern anchored to three columns reads the second shape as no scopes at all, which is how
    /// <c>auth:configure</c> came to be "missing" from a vocabulary that has seeded it since migration 0027.
    /// </remarks>
    private static HashSet<string> ScopesIn(string body)
    {
        // Everything from VALUES onward: the column list `(name, domain, description, …)` carries no quoted
        // literals, so it cannot be mistaken for a tuple, but starting after VALUES makes that explicit.
        var at = body.IndexOf("VALUES", StringComparison.Ordinal);
        var tuples = at < 0 ? body : body[at..];

        return new HashSet<string>(
            Regex.Matches(tuples, @"\(\s*'(?<name>[a-z]+[a-z0-9:_\-]*)'\s*,")
                .Select(m => m.Groups["name"].Value),
            StringComparer.Ordinal);
    }

    /// <summary>The text of every statement in the seed beginning with <paramref name="prefix"/>, up to its
    /// terminating semicolon, with the migration it came from. Isolating statements is what lets the
    /// shape-specific patterns above stay simple without matching tuples from a neighbouring INSERT; carrying
    /// the file name is what lets an unreadable one be reported by name.</summary>
    private static IEnumerable<(string File, string Body)> Statements(string prefix)
    {
        foreach (var (file, sql) in SeedFiles())
        {
            var at = 0;
            while ((at = sql.IndexOf(prefix, at, StringComparison.Ordinal)) >= 0)
            {
                var end = sql.IndexOf(';', at);
                if (end < 0) end = sql.Length - 1;
                yield return (file, sql[at..end]);
                at = end;
            }
        }
    }

    /// <summary>Roles that exist in the frozen vocabulary (they carry a sensitivity tier in the seed).</summary>
    private static HashSet<string> SeedRoles() =>
        new(Regex.Matches(SeedSql(), @"\('([a-z_]+)','T\d'\)").Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static string? _sql;
    private static IReadOnlyList<(string File, string Sql)>? _files;

    /// <summary>Every identity migration, in apply order, with <c>--</c> comments stripped. Stripping matters:
    /// statements are split on ';' and prose comments contain semicolons, which would silently truncate a
    /// statement mid-VALUES and drop the grants after it — a parser bug that reads exactly like a real gap.</summary>
    private static IReadOnlyList<(string File, string Sql)> SeedFiles() => _files ??=
    [
        .. Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "services", "identity", "Infrastructure", "Migrations"), "*.sql")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(path => (
                File: Path.GetFileName(path),
                Sql: string.Join('\n', File.ReadAllLines(path).Select(line =>
                {
                    var at = line.IndexOf("--", StringComparison.Ordinal);
                    return at < 0 ? line : line[..at];
                })))),
    ];

    /// <summary>The whole seed as one string, for the scans that are not statement-scoped.</summary>
    private static string SeedSql() => _sql ??= string.Join('\n', SeedFiles().Select(f => f.Sql));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
