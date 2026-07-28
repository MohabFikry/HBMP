using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// 21.2 — the guard on the guards (design 40 §7 invariants 3 and 5, ADR-0021).
///
/// Two tests in this repository are load-bearing in a way ordinary tests are not:
///
///   • THE A1 TEST — a platform administrator with no membership reaches administration keys and nothing
///     else. A1 is the adaptation that stops "platform admin" becoming a PHI wildcard over a refugee
///     population's medical records.
///   • THE PARITY SUITE — the effective set is computed in two places, and the design names their
///     divergence as the standing risk. The suite is the only thing that would notice.
///
/// Both are exactly the kind of test that gets deleted during a refactor with a reasonable-sounding
/// commit message, and neither failing is something anyone would notice afterwards — the system keeps
/// working, it just stops being checked. So their existence is itself asserted here, by name.
///
/// If you are here because this test failed: do not delete this test. Restore the one it names, or if the
/// work genuinely moved it, update the path — and say so in the PR, because it is a change to a control
/// that ADR-0021 records.
/// </summary>
public class AuthzParityGuardTests
{
    private static readonly (string Path, string[] Required)[] Pinned =
    [
        (Path.Combine("libs", "authz", "Tests", "EffectiveSetEvaluatorTests.cs"),
            ["A1_platform_admin_with_no_membership_reaches_no_clinical_or_benefit_key"]),

        (Path.Combine("services", "identity", "Tests", "EffectiveSetParityTests.cs"),
            ["Both_modes_compute_identical_sets", "Matrix"]),
    ];

    [Fact]
    public void The_A1_denial_test_and_the_parity_suite_still_exist()
    {
        var missing = new List<string>();

        foreach (var (relative, required) in Pinned)
        {
            var full = Path.Combine(RepoRoot(), relative);
            if (!File.Exists(full))
            {
                missing.Add($"{relative} — file is gone");
                continue;
            }

            var source = File.ReadAllText(full);
            missing.AddRange(required
                .Where(name => !source.Contains(name, StringComparison.Ordinal))
                .Select(name => $"{relative} — no longer contains '{name}'"));

            // A test that exists but is permanently skipped is worse than one that is missing: it reports
            // green. `Skip.If(IdentityTestDb.Conn is null, ...)` is the sanctioned, environment-driven
            // gate; an unconditional Skip is not.
            if (source.Contains("Skip.If(true", StringComparison.Ordinal) ||
                source.Contains("[Fact(Skip", StringComparison.Ordinal) ||
                source.Contains("[Theory(Skip", StringComparison.Ordinal) ||
                source.Contains("[SkippableFact(Skip", StringComparison.Ordinal))
                missing.Add($"{relative} — contains an unconditional skip");
        }

        missing.Should().BeEmpty(
            "the A1 denial test and the mode-1/mode-2 parity suite are pinned controls (ADR-0021); " +
            "removing or disabling one must fail the build, not pass quietly. Problems:\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The parity matrix has to keep covering the dimensions that make it worth running. A matrix trimmed
    /// to "roles only" would still be a parity suite by name and would prove nothing about overrides,
    /// expiry, deprecation or the platform-admin flag — the four places the two modes could actually
    /// disagree.
    /// </summary>
    [Fact]
    public void The_parity_matrix_still_covers_every_dimension()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "services", "identity", "Tests", "EffectiveSetParityTests.cs"));

        var dimensions = new (string Label, string Marker)[]
        {
            ("allow overrides", "allow adds a key"),
            ("deny overrides", "deny removes a role grant"),
            ("deny wins", "deny wins"),
            ("expiry", "expired allow is inert"),
            ("deprecation", "deprecated key still resolves"),
            ("platform-admin", "platform admin, no membership roles"),
            ("no membership authority", "no roles, no overrides"),
        };

        dimensions.Where(d => !source.Contains(d.Marker, StringComparison.Ordinal))
            .Select(d => d.Label).Should().BeEmpty("the parity matrix must keep exercising every dimension");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
