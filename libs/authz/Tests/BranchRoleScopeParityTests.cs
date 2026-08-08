using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Authz.Tests;

/// <summary>
/// 25.1 — THE TEST THAT PINS THE INVARIANT (design 42 §1/§7 rule 1, ADR-0029).
///
///   branch_coordinator and clinics_manager hold ONE permission set. They differ ONLY in reach.
///
/// The rejected alternative was two roles with two capability lists, and its failure mode is drift: someone
/// adds "revoke specialty" to the coordinator, forgets the manager, and the person supervising six clinics
/// can do less than the person running one of them. Nothing breaks — the manager's remedy is to ask a
/// coordinator, and asking works — so the gap survives until somebody happens to audit it.
///
/// That is why this is a test and not a comment. It reads the identity seed and asserts set equality in BOTH
/// directions, so a future phase that grants one role a scope fails the build until it grants both.
///
/// It runs WITHOUT a database on purpose. The DB-gated sibling
/// (identity <c>BranchRoleSeedTests</c>) proves the resolved runtime state; this one proves the source of
/// truth, and keeps proving it on a machine with no Postgres — the class of test 24.1 found silently
/// skipping for months.
/// </summary>
public class BranchRoleScopeParityTests
{
    private const string Coordinator = "branch_coordinator";
    private const string Manager = "clinics_manager";

    /// <summary>Reception's twelve (design 42 §1) — the set both branch roles inherit verbatim.</summary>
    private static readonly string[] ReceptionsTwelve =
    [
        "reception:search", "reception:read", "eligibility:check", "appointment:read", "appointment:write",
        "patient:read", "practitioner:read", "note:read", "profile:read", "callcentre:history:read",
        "notification:read", "claims:reimburse:submit",
    ];

    /// <summary>The four branch-scoped authorities this phase adds.</summary>
    private static readonly string[] TheFourBranchScopes =
    [
        "branch:practitioner:write", "branch:roster:write", "branch:inventory:read", "branch:inventory:write",
    ];

    [Fact]
    public void THE_INVARIANT_the_two_branch_roles_hold_exactly_the_same_scopes()
    {
        var seed = SeedGrants();

        seed.Should().ContainKey(Coordinator).And.ContainKey(Manager,
            "both branch roles must be granted scopes by the identity seed — a role with no grants is " +
            "seeded, assignable and silently powerless");

        var coordinator = seed[Coordinator];
        var manager = seed[Manager];

        // Both directions, reported separately: "which role gained something the other lacks" is the first
        // question anyone asks when this fails, and a bare set-inequality message does not answer it.
        manager.Except(coordinator).Should().BeEmpty(
            "clinics_manager holds a scope branch_coordinator lacks — one permission set, two reaches " +
            "(design 42 §1). Grant it to both, or to neither");
        coordinator.Except(manager).Should().BeEmpty(
            "branch_coordinator holds a scope clinics_manager lacks — the supervisor of six clinics would " +
            "be able to do less than the coordinator of one, which is the exact drift ADR-0029 rejects");

        coordinator.Should().BeEquivalentTo(manager);
    }

    [Fact]
    public void The_set_is_receptions_twelve_plus_the_four_branch_scopes()
    {
        // Pins the CONTENT as well as the equality. Without this, deleting both roles' grants — or granting
        // both of them one identical scope — would satisfy the equality test above and prove nothing.
        var expected = ReceptionsTwelve.Concat(TheFourBranchScopes).ToHashSet(StringComparer.Ordinal);

        SeedGrants()[Coordinator].Should().BeEquivalentTo(expected);
        SeedGrants()[Manager].Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void NEITHER_branch_role_holds_provider_write()
    {
        // Design 42 §7 rule 3. provider:write is network-wide: it creates branches and edits external labs,
        // pharmacies and tariffs, and it is the scope that unmasks license_no. A clinic coordinator needing
        // to maintain a doctor's licence must never acquire the authority to re-price the network to get it.
        var seed = SeedGrants();
        foreach (var role in new[] { Coordinator, Manager })
        {
            seed[role].Should().NotContain("provider:write",
                "'{0}' must never hold the network-wide provider scope (design 42 §7 rule 3)", role);
            seed[role].Should().NotContain("provider:admin", "'{0}' does not administer the network", role);
            seed[role].Should().NotContain("emr:read",
                "'{0}' runs the clinic; they do not read clinical notes (design 42 §1)", role);
        }
    }

    [Fact]
    public void The_reception_baseline_is_still_what_this_test_claims_it_is()
    {
        // Guards the guard, in the direction that matters. `ReceptionsTwelve` is a COPY of reception's grants,
        // and a copy is a thing that goes stale: if a later phase grants reception a thirteenth scope, the
        // branch roles are supposed to be reconsidered, not silently left behind while this file keeps
        // asserting a set that no longer describes the front desk.
        //
        // Deliberately one-directional — reception may hold scopes the branch roles do not — but every scope
        // this file calls "reception's" must still be one of reception's.
        var reception = SeedGrants()["reception"];
        foreach (var scope in ReceptionsTwelve)
            reception.Should().Contain(scope,
                "this test claims '{0}' is part of reception's set; it is no longer granted to reception, so " +
                "the branch roles' inheritance from reception needs revisiting", scope);
    }

    [Fact]
    public void The_scan_actually_read_the_seed()
    {
        // 24.1's lesson: a gate that runs and reads nothing is indistinguishable from a gate that passes.
        var seed = SeedGrants();
        seed.Should().HaveCountGreaterThan(10, "the identity seed grants scopes to every role in the catalog");
        seed[Coordinator].Should().HaveCount(16);
    }

    // ---- the seed, read straight out of the migrations ---------------------------------------------------
    //
    // Deliberately a separate, simpler reader from ScopeIntegrityTests.RoleScopes(): this file's whole job is
    // to be the thing that notices when the branch grants change, and sharing a parser with another test
    // would mean one parser bug could silence both.

    private static Dictionary<string, HashSet<string>> SeedGrants()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Grant(string role, string scope)
        {
            if (!map.TryGetValue(role, out var set)) map[role] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(scope);
        }

        foreach (var body in RoleScopeStatements())
        {
            // (a) explicit pairs
            foreach (Match m in Regex.Matches(body, @"\('([a-z_]+)',\s*'([a-z:_\-]+)'\)"))
                Grant(m.Groups[1].Value, m.Groups[2].Value);

            // (b) one scope, many roles
            foreach (Match m in Regex.Matches(body, @"SELECT role,\s*'([a-z:_\-]+)'\s*FROM \(VALUES(.*?)\) AS \w+\(role\)", RegexOptions.Singleline))
                foreach (Match r in Regex.Matches(m.Groups[2].Value, @"\('([a-z_]+)'\)"))
                    Grant(r.Groups[1].Value, m.Groups[1].Value);

            // (c) one role, many scopes
            foreach (Match m in Regex.Matches(body, @"SELECT '([a-z_]+)',\s*scope\s*FROM \(VALUES(.*?)\) AS \w+\(scope\)", RegexOptions.Singleline))
                foreach (Match sc in Regex.Matches(m.Groups[2].Value, @"\('([a-z:_\-]+)'\)"))
                    Grant(m.Groups[1].Value, sc.Groups[1].Value);

            // (d) 25.1 — many roles × many scopes from ONE list (the shape that makes seed-level drift
            // impossible: there is only one scope list, so it cannot disagree with itself).
            foreach (Match m in Regex.Matches(
                body,
                @"\(VALUES(?<roles>.*?)\) AS \w+\(role\)\s*CROSS JOIN \(VALUES(?<scopes>.*?)\) AS \w+\(scope\)",
                RegexOptions.Singleline))
                foreach (Match r in Regex.Matches(m.Groups["roles"].Value, @"\('([a-z_]+)'\)"))
                    foreach (Match sc in Regex.Matches(m.Groups["scopes"].Value, @"\('([a-z:_\-]+)'\)"))
                        Grant(r.Groups[1].Value, sc.Groups[1].Value);
        }
        return map;
    }

    private static IEnumerable<string> RoleScopeStatements()
    {
        var sql = SeedSql();
        const string prefix = "INSERT INTO identity.role_scope";
        var at = 0;
        while ((at = sql.IndexOf(prefix, at, StringComparison.Ordinal)) >= 0)
        {
            var end = sql.IndexOf(';', at);
            if (end < 0) end = sql.Length - 1;
            yield return sql[at..end];
            at = end;
        }
    }

    private static string? _sql;

    /// <summary>Every identity migration in apply order, with <c>--</c> comments stripped — prose contains
    /// semicolons, and splitting on ';' without stripping truncates a statement mid-VALUES and drops the
    /// grants after it, which reads exactly like a real gap.</summary>
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
