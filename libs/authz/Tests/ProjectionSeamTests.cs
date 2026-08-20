using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Authz.Tests;

/// <summary>
/// A policy rule that names no roles and a scope a person can hold are, together, an open door.
///
/// <para><b>The failure this exists for.</b> <see cref="IAuthorizationEngine"/> evaluates a rule's role list
/// as <c>rule.Roles.Count &gt; 0 &amp;&amp; !rule.Roles.Any(p.IsInRole)</c> — so an EMPTY role set means "any
/// authenticated principal holding the scope". That is exactly right for a machine seam: the outbox relay
/// authenticates as a client and carries no role at all, and naming one would deny the only legitimate
/// caller. It is exactly wrong for anything a person can hold.</para>
///
/// <para><c>ReportingPolicies.Project</c> and <c>FinancePolicies.Project</c> were both roleless, and the
/// identity seed granted <c>reporting:project</c> to <c>medical_director</c> and <c>finance:project</c> to
/// <c>finance</c>. So a Medical Director's own browser token authorized a write into
/// <c>authorization_fact</c>, <c>pending_authorization</c>, <c>encounter_fact</c>, <c>utilization_fact</c>,
/// <c>code_count</c> and <c>financial_fact</c> — the six tables their own turnaround, SLA-breach, no-show,
/// rejection and cost figures are computed from — and the handler wrote no audit event. A finance officer
/// held the same power over the cost ledger the finance report is built from.</para>
///
/// <para><b>Why no existing test caught it.</b> <see cref="ScopeIntegrityTests"/> checks that every role named
/// by a rule can hold one of its scopes, and opens with
/// <c>if (rule.Roles.Count == 0 || rule.Scopes.Count == 0) continue;</c> — it skips precisely the rules this
/// file is about. The two checks are complements: that one asks whether a named role can reach a rule, this
/// one asks whether an unnamed one should.</para>
///
/// <para><b>What is asserted.</b> The two halves of the pairing, separately, because either alone is
/// insufficient. A roleless rule must require only <c>service_only</c> scopes; and no role may be granted a
/// <c>service_only</c> scope, since the seeded roles are inserted by SQL and never pass through the
/// tenant-local role editor's own refusal of machine keys.</para>
/// </summary>
public class ProjectionSeamTests
{
    /// <summary>Every staff-facing bundle. Mirrors <see cref="ScopeIntegrityTests"/>'s list, including its
    /// reason for omitting InteropPolicies: <c>fhir:*</c> is granted per external CLIENT under a data-sharing
    /// agreement, so <c>role_scope</c> is the wrong table to look those up in (ADR-0016).</summary>
    private static readonly PolicyBundle[] Bundles =
    [
        AdminPolicies.Bundle(), PatientPolicies.Bundle(), ClaimsPolicies.Bundle(), EmrPolicies.Bundle(),
        OrdersPolicies.Bundle(), PharmacyPolicies.Bundle(), ApprovalsPolicies.Bundle(), CasePolicies.Bundle(),
        FinancePolicies.Bundle(), ProviderPolicies.Bundle(), ReportingPolicies.Bundle(),
        NotificationPolicies.Bundle(), DocumentPolicies.Bundle(), CallCentrePolicies.Bundle(),
        PolicyPolicies.Bundle(), ProfilePolicies.Bundle(),
    ];

    /// <summary>
    /// Roleless rules whose scope is deliberately person-holdable, each with the reason.
    ///
    /// <para>An explicit register rather than a softened assertion, for the same reason
    /// <c>ProjectionFeedTests.KnownUnfed</c> keeps one: a rule that is understood to be open to any
    /// authenticated caller is a different thing from one nobody has looked at, and the difference has to be
    /// written down or the check decays into "some of these are fine".</para>
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyOpenToAnyRole = new(StringComparer.Ordinal)
    {
        ["notification:read|notification"] =
            "the in-app inbox. Every portal has one and the notification-service row-filters by recipient == "
            + "caller, so the rule is inherently minimum-necessary: an unrestricted role list here grants "
            + "each caller their own messages and nobody else's.",

        ["document:write|document"] =
            "uploading and the operational read over what was uploaded. The scope IS the role list here: "
            + "identity 0016 grants document:write to an enumerated set of roles and the rule declines to "
            + "state it twice, because two copies of one list are two lists that drift. Person-holdable on "
            + "purpose — a document upload is an ordinary act of the people who hold the scope.",

        ["policy:read|policy"] =
            "reading the benefit configuration. Every role that adjudicates against a benefit needs to see "
            + "the rules it is judged by, so the entitled set is 'whoever was granted the scope' rather than "
            + "a list this rule would have to keep in step with. Tenant-scoped and carries no PHI.",

        ["eligibility:check|policy"] =
            "the second scope satisfying policy:price-lookup — the version in force on a date and one "
            + "category's cost share at one tier. It can disclose nothing but the terms of the plan the "
            + "caller is already quoting from, so a holder of either scope is entitled to it.",
    };

    [Fact]
    public void A_rule_that_names_no_roles_may_only_require_a_machine_key()
    {
        var machine = MachineScopes();
        var open = new List<string>();

        foreach (var rule in Bundles.SelectMany(b => b.Rules).DistinctBy(r => $"{r.Action}|{r.ResourceType}"))
        {
            if (rule.Roles.Count > 0) continue;

            // A rule with neither a role nor a scope is a different defect and is not this test's business;
            // the engine's own default-deny and the endpoint's RequireAuthorization still stand in front of it.
            if (rule.Scopes.Count == 0) continue;

            foreach (var scope in rule.Scopes.Order(StringComparer.Ordinal))
            {
                if (machine.Contains(scope)) continue;
                if (DeliberatelyOpenToAnyRole.ContainsKey($"{scope}|{rule.ResourceType}")) continue;
                open.Add(
                    $"{rule.Action} on {rule.ResourceType}: the rule names no roles — which the engine reads "
                    + $"as ANY authenticated principal — and requires '{scope}', which is not marked "
                    + "service_only in the identity catalogue, so a person can hold it");
            }
        }

        open.Should().BeEmpty(
            "a roleless rule over a person-holdable scope is an open door that reads like a narrow one. Some "
            + "of them are deliberate — a scope grant can legitimately BE the whole authority, and four "
            + "rules use it that way — but that has to be a decision somebody wrote down rather than the "
            + "default a new rule inherits by leaving a field unset. So: mark the scope service_only and "
            + "revoke the role grants, or name the roles on the rule, or add it to "
            + "DeliberatelyOpenToAnyRole with the reason:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, open));
    }

    [Fact]
    public void No_role_is_granted_a_machine_key()
    {
        // Coarse ON PURPOSE: any mention of the scope name inside a role_scope INSERT is treated as a grant.
        // The seed grants in seven shapes — tuple lists, tenant fan-outs, SELECT DISTINCT over existing
        // grants, role-to-role copies — and a pattern that understood only the simple one would report a
        // fan-out grant as no grant at all. A name that appears in a role_scope INSERT for any reason is
        // worth a human look; a false positive here costs a comment, a false negative costs the invariant.
        var machine = MachineScopes();
        var granted = new List<string>();

        foreach (var (file, body) in Statements("INSERT INTO identity.role_scope"))
            foreach (var scope in machine.Order(StringComparer.Ordinal))
                if (body.Contains($"'{scope}'", StringComparison.Ordinal))
                    granted.Add($"{file} grants the machine key '{scope}' to a role");

        granted.Should().BeEmpty(
            "the tenant-local role editor refuses to attach a service_only scope to a role — 'a service "
            + "credential attached to a person, and no review would ever catch it as one' — but the built-in "
            + "roles are seeded straight into role_scope by SQL and never pass through that check, which is "
            + "how reporting:project reached medical_director:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, granted));
    }

    [Fact]
    public void The_open_register_does_not_go_stale()
    {
        // An entry whose rule stopped being roleless, or stopped existing, is an excuse outliving its reason.
        var roleless = Bundles.SelectMany(b => b.Rules)
            .Where(r => r.Roles.Count == 0)
            .SelectMany(r => r.Scopes.Select(s => $"{s}|{r.ResourceType}"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (key, reason) in DeliberatelyOpenToAnyRole)
            roleless.Should().Contain(key,
                "'{0}' is registered as deliberately open, but no roleless rule requires it any more ({1})", key, reason);

        DeliberatelyOpenToAnyRole.Values.Should().OnlyContain(r => r.Length > 40,
            "the reasons must be reasons, not placeholders");
    }

    [Fact]
    public void The_scan_reads_the_flag_and_not_merely_the_names()
    {
        // Guards the guard. A parser that returned an empty machine set would make the first test vacuously
        // green and the second trivially so — the exact failure mode a regex scraper has, since it does not
        // crash on a shape it cannot read, it just returns less.
        var machine = MachineScopes();

        machine.Should().Contain(
            ["auth:ingest", "claims:ingest", "notification:ingest", "reporting:project", "finance:project"],
            "these five are the platform's machine seams and are declared service_only in the identity seed; "
            + "if the parse cannot find them it is reading names without the flag");

        machine.Should().NotContain("reporting:read",
            "an ordinary interactive scope must not come back as a machine key — that would make the first "
            + "test pass for the wrong reason");
    }

    // ---- the catalogue, read out of the migrations ------------------------------------------------------

    /// <summary>
    /// Scopes declared <c>service_only</c> in the identity seed.
    /// </summary>
    /// <remarks>
    /// <para>Reads the three-column tuple form — <c>('name','domain',true)</c> — which is what every machine
    /// key in the seed uses. The six-column form (<c>name, domain, description, service_only, deprecated,
    /// is_platform_admin_key</c>) that later migrations use carries a multi-line quoted description between
    /// the name and the flag, and every scope declared that way is interactive; a scope not matched here is
    /// therefore read as person-holdable, which is the SAFE direction to be wrong in — it can only make the
    /// assertion above stricter, never quieter.</para>
    /// </remarks>
    private static HashSet<string> MachineScopes()
    {
        var machine = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, body) in Statements("INSERT INTO identity.scope"))
        {
            var at = body.IndexOf("VALUES", StringComparison.Ordinal);
            var tuples = at < 0 ? body : body[at..];
            foreach (Match m in Regex.Matches(tuples,
                @"\(\s*'(?<name>[a-z][a-z0-9:_\-]*)'\s*,\s*'[a-z]+'\s*,\s*(?<flag>true|false)\s*\)"))
                if (m.Groups["flag"].Value == "true") machine.Add(m.Groups["name"].Value);
        }
        return machine;
    }

    /// <summary>Every statement in the seed beginning with <paramref name="prefix"/>, up to its terminating
    /// semicolon, with the migration it came from. Isolating statements is what keeps a tuple from a
    /// neighbouring INSERT out of the match.</summary>
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

    private static IReadOnlyList<(string File, string Sql)>? _files;

    /// <summary>Every identity migration in apply order, with <c>--</c> comments stripped — prose comments
    /// contain semicolons, and statements are split on them.</summary>
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
