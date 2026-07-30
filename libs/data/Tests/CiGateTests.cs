using System.Text.Json;
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

    /// <summary>
    /// The drift gate must refuse to compare NOTHING.
    ///
    /// <para>It copied <c>artifacts/openapi/*.json</c> over docs/api with <c>2&gt;/dev/null || true</c>. When
    /// generation produced no specs at all the glob matched no files, the copy was swallowed, <c>git diff</c>
    /// was clean, and the gate reported the contract verified — so the louder the upstream failure, the
    /// greener this gate got. Nothing verified and nothing changed are not the same finding, and only one of
    /// them is safe to report as a pass.</para>
    /// </summary>
    [Fact]
    public void Openapi_drift_fails_rather_than_comparing_an_empty_spec_directory()
    {
        var workflow = Workflow();
        var step = Regex.Match(workflow, @"(?s)gate: OpenAPI drift.*?\n(?=      - )").Value;
        step.Should().NotBeEmpty("the drift gate step must be findable for this assertion to mean anything");

        step.Should().NotContain("cp artifacts/openapi/*.json docs/api/ 2>/dev/null || true",
            "that form turns 'the generator produced nothing' into a clean diff and a PASS");
        step.Should().MatchRegex(@"ls artifacts/openapi/\*\.json[\s\S]*?wc -l",
            "the gate must count what it is about to compare");
        step.Should().MatchRegex(@"-eq 0[\s\S]*?exit 1",
            "zero generated specs must fail the gate, not pass it silently");
    }

    /// <summary>
    /// Fail-at-end only works if every deferred failure is actually counted.
    ///
    /// <para><c>continue-on-error: true</c> means "record this and keep going"; the summary step at the end
    /// then reads each step's outcome and fails the job. A step that carries continue-on-error but has no
    /// <c>id</c> in that summary is therefore a step that CANNOT fail the build — the strongest possible
    /// green-washing, and invisible in review because the line reads exactly like its neighbours.</para>
    ///
    /// <para>Two were found this way. The OpenAPI generation step could fail for all 21 services with the job
    /// staying green (and the drift gate downstream then had nothing to compare, so it passed too). Worse,
    /// the step exporting <c>*_TEST_DB</c> — without which every DB-gated concurrency, RLS-isolation and
    /// break-glass test answers Skip.If(...) — was equally uncounted, so the suite could report green having
    /// proven none of them.</para>
    ///
    /// <para>Some steps are legitimately advisory. Those say so in a comment, in the file, next to the step,
    /// with the reason their failure cannot weaken a verdict — which is reviewable, unlike an omission.</para>
    /// </summary>
    [Fact]
    public void Every_deferred_failure_is_either_counted_or_declared_advisory()
    {
        var workflow = Workflow();
        var outcomes = Regex.Match(workflow, @"(?s)OUTCOMES:\s*\|(.*?)\n        run:").Groups[1].Value;
        outcomes.Should().NotBeEmpty("the summary's OUTCOMES map must be parseable");
        var counted = Regex.Matches(outcomes, @"steps\.(\w+)\.outcome")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        // Walk the steps line by line rather than splitting on a regex: a step's ADVISORY note sits ABOVE
        // the `- name:` it excuses, so the comment lines immediately preceding a step have to be read as
        // part of it — attach them to the previous step and the note excuses the wrong one.
        var lines = workflow.Split('\n');
        var uncounted = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var head = Regex.Match(lines[i], @"^      - (?:name|uses): (.*)$");
            if (!head.Success) continue;

            var body = new List<string>();
            for (var j = i - 1; j >= 0 && lines[j].StartsWith("      #", StringComparison.Ordinal); j--)
                body.Add(lines[j]);                                   // the note above the step
            for (var j = i + 1; j < lines.Length && !Regex.IsMatch(lines[j], @"^      - (?:name|uses):"); j++)
            {
                if (lines[j].StartsWith("      #", StringComparison.Ordinal)) break;  // next step's note
                body.Add(lines[j]);
            }
            var block = string.Join('\n', body);
            if (!block.Contains("continue-on-error: true", StringComparison.Ordinal)) continue;

            var id = Regex.Match(block, @"(?m)^\s*id: (\w+)").Groups[1].Value;
            if (id.Length > 0 && counted.Contains(id)) continue;
            if (block.Contains("# ADVISORY:", StringComparison.Ordinal)) continue;
            uncounted.Add(head.Groups[1].Value.Trim().Trim('"'));
        }

        uncounted.Should().BeEmpty(
            "these steps defer their failure and nothing collects it, so they can never fail the build — " +
            "give each an id listed in OUTCOMES, or a '# ADVISORY:' comment saying why its failure cannot " +
            "weaken a verdict:{0}  {1}", Environment.NewLine, string.Join($"{Environment.NewLine}  ", uncounted));

        // And the summary must actually fail the job on a non-success outcome, or counting is decoration.
        workflow.Should().Contain("exit 1", "the gate summary must fail the job when any outcome is not success");
    }

    /// <summary>
    /// A green suite is only meaningful with the DB-gated tests actually running (CLAUDE.md). The env that
    /// decides that is exported by one step, so that step verifies its own EFFECT rather than trusting an
    /// exit code — a script that succeeds while printing nothing would otherwise leave every
    /// Skip.If(&lt;SERVICE&gt;_TEST_DB is null) answered "skip" with the run still reporting green.
    /// </summary>
    [Fact]
    public void The_test_db_wiring_step_checks_that_the_variables_actually_landed()
    {
        var step = Regex.Match(Workflow(), @"(?s)gate: wire \*_TEST_DB.*?\n(?=      - )").Value;
        step.Should().NotBeEmpty("the *_TEST_DB wiring must be a named gate, not an anonymous step");
        step.Should().Contain("_TEST_DB=", "it must count the variables it exported");
        step.Should().Contain("exit 1", "too few exported variables must fail, not pass quietly");
    }

    /// <summary>
    /// A gate's INPUTS are part of its trigger.
    ///
    /// <para>The workflow's <c>paths:</c> filter decides whether the job runs at all, so a path missing from
    /// it is a path no gate in this pipeline can ever see — and the commit still shows a clean tick, because
    /// no check was ever required. It is the failure mode of 24.1 (a gate that cannot go blind) hiding one
    /// level above the gates.</para>
    ///
    /// <para>It happened: <c>docs/api</c> holds the committed OpenAPI contracts and the drift gate exists to
    /// compare them with the running services, but no <c>docs/**</c> entry was listed. The one commit whose
    /// entire content was a contract change — 9f817a1, restoring 14 endpoints that had gone missing from the
    /// spec — triggered no workflow run at all. The gate guarding the contract could not see commits that
    /// change the contract, which is the only kind of commit it exists to inspect.</para>
    ///
    /// <para>So this does not hold a hand-written list. It reads the scripts the workflow actually invokes,
    /// collects the <c>docs/</c> trees they mention, and requires each one in the filter — a gate added
    /// tomorrow that reads a new docs tree cannot quietly fail to trigger on it.</para>
    /// </summary>
    [Fact]
    public void The_workflow_triggers_on_every_file_a_gate_reads()
    {
        var workflow = Workflow();

        var trees = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(workflow, @"tools/ci/[A-Za-z0-9._-]+"))
        {
            var script = Path.Combine(RepoRoot(), m.Value.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(script)) continue;
            foreach (Match d in Regex.Matches(File.ReadAllText(script), @"\bdocs/([a-z][a-z0-9_-]*)/"))
                trees.Add(d.Groups[1].Value);
        }
        // ...plus the trees the workflow's own inline steps touch (docs/api, via the drift gate).
        foreach (Match d in Regex.Matches(workflow, @"\bdocs/([a-z][a-z0-9_-]*)\b"))
            trees.Add(d.Groups[1].Value);

        trees.Should().Contain("api", "the OpenAPI drift gate compares docs/api — this test is worthless if " +
            "it cannot even see that reference, so a failure here means the discovery regex stopped matching");

        var missing = trees.Where(t => !workflow.Contains($"\"docs/{t}/**\"", StringComparison.Ordinal)).ToList();
        missing.Should().BeEmpty(
            "a gate in this workflow reads docs/{0} but the paths filter does not list it, so a commit that " +
            "changes only those files starts no run and is merged with every check unrun and none reported red",
            string.Join(", docs/", missing));

        // Both triggers, not just push: a pull_request filter that omits the tree lets the change arrive
        // through the door where review is supposed to be strongest with the gate not running.
        var push = Regex.Match(workflow, @"(?s)\bpush:\s*\n\s*paths:\n(.*?)\n\s*pull_request:").Groups[1].Value;
        var pr = Regex.Match(workflow, @"(?s)\bpull_request:\s*\n\s*paths:\n(.*?)\n\s*\n").Groups[1].Value;
        push.Should().NotBeEmpty("the push paths filter must be parseable for this assertion to mean anything");
        pr.Should().NotBeEmpty("the pull_request paths filter must be parseable");
        foreach (var tree in trees)
        {
            push.Should().Contain($"\"docs/{tree}/**\"", "push must trigger on docs/{0}", tree);
            pr.Should().Contain($"\"docs/{tree}/**\"", "pull_request must trigger on docs/{0}", tree);
        }
    }

    [Fact]
    public void Identity_tests_get_a_database_in_ci()
    {
        var env = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "ci", "print-test-db-env.sh"));
        env.Should().Contain("IDENTITY",
            "identity-service's DB-gated tests skipped in CI because nothing exported IDENTITY_TEST_DB");
    }

    /// <summary>
    /// The floors are real, and there is exactly ONE copy of them.
    ///
    /// <para>24.1.3 moved the floors out of the workflow env — where this test used to read them — into
    /// tools/ci/coverage-floors.json. The assertion has followed its subject rather than been relaxed: it
    /// still proves the domain floor sits above the abandoned 55 and no higher than the documented target,
    /// and it now additionally proves what the old arrangement could not — that no second copy survives in
    /// either pipeline. Three files once claimed three different bars (80 in .gitlab-ci.yml, 58 in the
    /// workflow env, a third default inside coverage-gate.sh) and only one of them was enforced, so
    /// "what is the coverage bar?" had three answers depending on which file you opened.</para>
    /// </summary>
    [Fact]
    public void The_coverage_floors_live_in_exactly_one_place_and_gate_overall_too()
    {
        var floorsPath = Path.Combine(RepoRoot(), "tools", "ci", "coverage-floors.json");
        File.Exists(floorsPath).Should().BeTrue("coverage-floors.json is the single source of truth");

        using var floors = JsonDocument.Parse(File.ReadAllText(floorsPath));
        var aggregates = floors.RootElement.GetProperty("aggregates");

        var domain = aggregates.GetProperty("domain").GetInt32();
        domain.Should().BeGreaterThan(55, "the floor was set at 55 as a temporary regression guard and never raised");
        domain.Should().BeLessThanOrEqualTo(80, "80 is the documented target (CLAUDE.md); overshooting it here would be a lie of a different kind");

        aggregates.TryGetProperty("overall", out _).Should().BeTrue(
            "overall coverage was printed and not gated — it is the number that falls when the DB-gated " +
            "suites stop running, which is exactly the failure a green build must not hide");

        // The gate must READ the file rather than carry its own default, or the file is decoration.
        var gate = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "ci", "coverage-gate.sh"));
        gate.Should().Contain("coverage-floors.json", "the gate must read the floors from the one file");

        // And no competing copy anywhere else. Matched as a real YAML ASSIGNMENT at the start of a line,
        // not as a substring: both files carry comments explaining which value used to live there and why it
        // was removed, and a test that forbids naming the thing you deleted forces the history out of the
        // file — which is how the reason for a change gets lost.
        Regex.IsMatch(Workflow(), @"(?m)^\s*COVERAGE_MIN_DOMAIN\s*:").Should().BeFalse(
            "a floor in the workflow env is a second source of truth; lowering it there reads like a config tweak");
        Regex.IsMatch(File.ReadAllText(Path.Combine(RepoRoot(), ".gitlab-ci.yml")), @"(?m)^\s*COVERAGE_MIN\s*:")
            .Should().BeFalse("GitLab carried an unused COVERAGE_MIN of 80 that contradicted the enforced floor");
    }

    /// <summary>
    /// A floor that only moves when somebody remembers is a floor that never moves — CLAUDE.md has asked
    /// for 80% domain since the beginning and the enforced value sat at 58. The ratchet has to be wired,
    /// not merely written, so this asserts both guards exist and that CI actually runs the monotonicity one.
    /// </summary>
    [Fact]
    public void The_floor_ratchet_is_wired_in_both_directions()
    {
        var tools = Path.Combine(RepoRoot(), "tools", "ci");
        File.Exists(Path.Combine(tools, "check-floor-monotonicity.py")).Should().BeTrue(
            "a floor that can be lowered in a quiet diff is not a floor");
        File.Exists(Path.Combine(tools, "raise-floors.py")).Should().BeTrue(
            "without the auto-raise the ratchet only ever protects the value someone set months ago");

        Workflow().Should().Contain("check-floor-monotonicity.py",
            "the monotonicity guard must run in CI, not merely exist in the repo — that was the whole " +
            "lesson of the month the coverage gate never executed");
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
