using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Data.Tests;

/// <summary>
/// Phase 18.E1 (audit R2 Q1/Q2/Q4) — the gates must enforce what they claim.
///
/// Three of the findings in this gate were not missing checks; they were checks that EXISTED and did not
/// run, or ran against the wrong scope, or announced a threshold they never applied. That is the worst
/// category, because the build is green and the dashboard says "gated":
///   • check-kong-route-coverage.py was written, committed, and never wired into CI — and inspected only
///     /api/v1, so it was structurally blind to the gap that shipped as W3 (the whole FHIR facade).
///   • identity-service — the newest service, and the one that mints every token — had no *_TEST_DB entry
///     and no OpenAPI spec, so its DB-gated tests silently skipped in CI and its contract was ungoverned.
///   • The coverage floor was set to 55 as a regression guard "to be raised over time" and never moved,
///     while overall coverage was printed and not gated at all.
///
/// These assert the WIRING, because a script nobody calls is indistinguishable from a script that passes.
/// </summary>
public class CiGateTests
{
    private static string Workflow() => File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "backend-ci.yml"));

    [Fact]
    public void The_kong_route_coverage_guard_runs_in_ci()
    {
        Workflow().Should().Contain("tools/ci/check-kong-route-coverage.py",
            "the guard existed since 16.8 and was never invoked — an unwired gate is not a gate");
    }

    [Fact]
    public void The_kong_guard_checks_every_public_prefix_not_just_api_v1()
    {
        // The specific blindness that let W3 through: interop serves /fhir, so a guard scoped to /api/v1
        // could never have seen it. Same for /identity (W5).
        var guard = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "ci", "check-kong-route-coverage.py"));
        foreach (var prefix in new[] { "/api/v1", "/fhir", "/interop", "/identity", "/connect" })
            guard.Should().Contain($"\"{prefix}\"", "the guard must cover {0}", prefix);
    }

    [Fact]
    public void Identity_and_interop_are_in_the_openapi_generation_list()
    {
        var generator = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "ci", "generate-openapi.sh"));
        var list = Regex.Match(generator, @"for k in (.*?); do", RegexOptions.Singleline).Groups[1].Value;
        list.Should().Contain("Identity").And.Contain("Interop");
    }

    [Fact]
    public void Every_service_with_swagger_has_a_committed_openapi_spec()
    {
        // The drift check is only meaningful if the specs are actually committed. Generated-and-uploaded to
        // an artefact nobody opens is what let the committed contract and the running services diverge.
        var apiDir = Path.Combine(RepoRoot(), "docs", "api");
        Directory.Exists(apiDir).Should().BeTrue("docs/api holds the committed contracts");

        var committed = Directory.EnumerateFiles(apiDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)!).ToHashSet(StringComparer.Ordinal);

        foreach (var svcDir in Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "services")))
        {
            var svc = Path.GetFileName(svcDir)!;
            if (svc == "hello") continue;
            var api = Path.Combine(svcDir, "Api");
            if (!Directory.Exists(api)) continue;
            var usesSwagger = Directory.EnumerateFiles(api, "*.cs")
                .Any(f => File.ReadAllText(f).Contains("AddSwaggerGen", StringComparison.Ordinal));
            if (!usesSwagger) continue;

            committed.Should().Contain(svc,
                "{0}-service exposes an OpenAPI document but has no committed spec in docs/api — its contract " +
                "can change without anyone seeing it in a pull request", svc);
        }
    }

    [Fact]
    public void Openapi_drift_is_a_red_build()
    {
        Workflow().Should().Contain("git diff --exit-code -- docs/api",
            "generating a spec and discarding it lets the committed contract drift from the running service");
    }

    [Fact]
    public void Identity_tests_get_a_database_in_ci()
    {
        var env = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "ci", "print-test-db-env.sh"));
        env.Should().Contain("IDENTITY",
            "identity-service's DB-gated tests skipped in CI because nothing exported IDENTITY_TEST_DB");
    }

    [Fact]
    public void The_coverage_floor_has_moved_off_its_original_value_and_gates_overall_too()
    {
        var workflow = Workflow();
        var domain = int.Parse(Regex.Match(workflow, @"COVERAGE_MIN_DOMAIN:\s*""(\d+)""").Groups[1].Value);
        domain.Should().BeGreaterThan(55, "the floor was set at 55 as a temporary regression guard and never raised");
        domain.Should().BeLessThanOrEqualTo(80, "80 is the documented target (CLAUDE.md); overshooting it here would be a lie of a different kind");

        workflow.Should().Contain("COVERAGE_MIN_OVERALL",
            "overall coverage was printed and not gated — it is the number that falls when the DB-gated " +
            "suites stop running, which is exactly the failure a green build must not hide");
    }

    [Fact]
    public void There_is_one_implementation_of_each_gate_across_both_pipelines()
    {
        // The split-brain: two pipelines gating one repo, and the GitLab one announced a coverage threshold
        // it never enforced. Both now call the same scripts, so a gate cannot be strong in one and weak in
        // the other. ADR-0001 records the decision.
        var gitlab = File.ReadAllText(Path.Combine(RepoRoot(), ".gitlab-ci.yml"));
        gitlab.Should().Contain("tools/ci/coverage-gate.sh", "the coverage gate must be the shared script");
        gitlab.Should().Contain("tools/ci/check-kong-route-coverage.py");
        gitlab.Should().NotContain("Coverage threshold: ${COVERAGE_MIN}% on domain projects",
            "this line announced a gate that did not exist");

        var adr = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "adr", "0001-cicd-platform.md"));
        adr.Should().Contain("18.E1", "the split-brain resolution must be recorded in the ADR that caused it");
    }

    [Fact]
    public void No_db_gated_test_silently_passes_by_returning_early()
    {
        // A `return` inside a [Fact] reports PASSED. On a machine without the env var the test contributes a
        // green tick having run nothing — which reads as coverage. SkippableFact reports SKIPPED.
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "services"), "*Tests*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f =>
            {
                var src = File.ReadAllText(f);
                // An early return guarding on a *_TEST_DB-derived field, inside a [Fact] (not SkippableFact).
                return Regex.IsMatch(src, @"\[Fact\][\s\S]{0,400}?if \((?:Db|Owner|App|_db)\b[^)]*\bis null[^)]*\) return;");
            })
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        offenders.Should().BeEmpty(
            "these report PASSED without running:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
