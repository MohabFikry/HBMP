using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// Every host the gateway forwards to is something that actually runs.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>inventory-service</c> was routed from Kong at 25.5 — schema migrated, service
/// building, its 68 tests passing — with no compose service and no Dockerfile. So <c>/api/v1/inventory</c>
/// resolved to a hostname that does not exist on the network, every request from the Inventory screen died
/// at name resolution, and the SPA reported "the service couldn't complete this request": the wording of a
/// transient fault, for an upstream that had never been deployed at all. The screen shipped that way and
/// stayed that way.</para>
///
/// <para><b>A route to nowhere is worse than a missing route.</b> A missing one 404s at the edge, which reads
/// as "not built yet". A route to a host nobody runs looks exactly like an outage of something that exists,
/// so the first thing anyone does is go looking for the outage.</para>
///
/// <para><b>Why the existing gate did not catch it.</b> <c>check-kong-route-coverage.py</c> asks the opposite
/// direction: does every public resource this platform SERVES have a route. It has caught several real
/// defects and it is why the day roster, the booking reads and the availability CRUD all have routes.
/// Nothing asked whether every host Kong FORWARDS TO is deployed.</para>
///
/// <para><b>Stated twice, deliberately.</b> <c>tools/ci/check-kong-upstreams.py</c> is the same rule in the
/// lint lane, where it fails in seconds and before anything is built. This is the copy the invariant registry
/// can name, and the copy that runs in the ordinary test suite. They read the same two files and answer the
/// same question; if they ever disagree, one of them is wrong and both are worth looking at.</para>
/// </remarks>
public class KongUpstreamsAreDeployedTests
{
    /// <summary>Hosts Kong may forward to that are deliberately NOT compose services. Empty today; anything
    /// added here needs a sentence saying what runs it, because "it is somewhere else" is a claim somebody
    /// has to make on purpose.</summary>
    private static readonly HashSet<string> AllowedExternal = [];

    [Fact]
    public void Every_kong_upstream_is_a_service_the_stack_actually_runs()
    {
        var upstreams = KongUpstreams();
        var services = ComposeServices();

        upstreams.Should().NotBeEmpty("a gate that reads no upstreams passes on an empty set");
        services.Should().HaveCountGreaterThan(10, "refusing to judge upstreams against a compose file this scan failed to read");

        var missing = upstreams.Where(h => !services.Contains(h) && !AllowedExternal.Contains(h)).ToList();

        missing.Should().BeEmpty(
            "kong forwards to these hosts and nothing in infra/compose/compose.yaml runs them — add the "
            + "service (and give it a Dockerfile), or remove its route");
    }

    private static IReadOnlyList<string> KongUpstreams() =>
        [.. Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), "infra", "compose", "config", "kong.yml")),
                @"^\s*url:\s*https?://([A-Za-z0-9_.-]+)(?::\d+)?", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Distinct()];

    /// <summary>
    /// Service keys from inside the top-level <c>services:</c> block only.
    ///
    /// <c>volumes:</c> and <c>networks:</c> indent their children identically, so scanning the whole file
    /// counts a volume as a service — which would only ever make this gate more permissive, and that is the
    /// direction a guard must not drift.
    /// </summary>
    private static HashSet<string> ComposeServices()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "compose", "compose.yaml"));
        var start = Regex.Match(compose, @"^services:\s*$", RegexOptions.Multiline);
        if (!start.Success) return [];

        var rest = compose[(start.Index + start.Length)..];
        var end = Regex.Match(rest, @"^[a-z][a-z0-9_-]*:", RegexOptions.Multiline);
        var block = end.Success ? rest[..end.Index] : rest;

        return [.. Regex.Matches(block, @"^  ([a-z0-9][a-z0-9_-]*):\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
