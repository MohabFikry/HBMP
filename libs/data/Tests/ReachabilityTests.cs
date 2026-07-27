using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Data.Tests;

/// <summary>
/// Phase 18.C2 (audit R2 W3/W5/W6) — every service that exists is deployed, and every deployed service is
/// routable.
///
/// Three capabilities were finished, tested and unreachable. interop/FHIR (all of phase 13) had no compose
/// block and no Kong route. identity's in-app admin — the thing that closes audit finding C3 — had no route,
/// so the console went on editing the legacy projection. The SPA itself was commented out of compose, so
/// `docker compose up` brought up twenty services and no user interface.
///
/// None of it failed anything. A service with no route does not error; it simply is not there, and the only
/// signal is a person eventually asking "why can't I get to this?". The API coverage guard could not see it
/// either — it inspects `/api/v1`, and interop deliberately serves `/fhir`. So this reads the deployment
/// descriptors directly and fails the build when a service, a route or the SPA goes missing.
/// </summary>
public class ReachabilityTests
{
    /// <summary>Services that must have BOTH a compose block and a Kong route, with the route prefix each
    /// one actually serves. The prefix matters: assuming `/api/v1` is exactly how interop stayed invisible.</summary>
    private static readonly (string Service, string RoutePrefix)[] Routable =
    [
        ("interop-service", "/fhir"),
        ("identity-service", "/identity"),
        ("claims-service", "/api/v1/claims"),
        ("callcentre-service", "/api/v1/call-interactions"),
        ("admin-service", "/api/v1/admin"),
    ];

    [Fact]
    public void Every_routable_service_has_a_compose_block()
    {
        var compose = Compose();
        foreach (var (service, _) in Routable)
            compose.Should().Contain($"\n  {service}:",
                "{0} cannot be reached if it is not deployed", service);
    }

    [Fact]
    public void Every_routable_service_has_a_kong_route_for_the_prefix_it_serves()
    {
        var kong = Kong();
        foreach (var (service, prefix) in Routable)
        {
            kong.Should().Contain($"name: {service}", "{0} needs a Kong service entry", service);
            kong.Should().Contain($"\"{prefix}\"",
                "{0} serves {1}; without that path in its route the gateway has nowhere to send the call",
                service, prefix);
        }
    }

    [Fact]
    public void The_web_ui_is_deployed_and_not_commented_out()
    {
        var compose = Compose();
        compose.Should().Contain("\n  web:",
            "the SPA was commented out of compose since phase 9 — the stack came up with no user interface");
        // A commented block would satisfy a naive Contains, so assert the service is real by requiring the
        // build context that only an active block carries.
        var web = Block(compose, "  web:");
        web.Should().Contain("dockerfile: apps/web/Dockerfile");
        web.Should().Contain("VITE_OIDC_AUTHORITY", "the SPA needs the issuer origin baked into its bundle");
    }

    [Fact]
    public void The_fhir_capability_statement_stays_publicly_reachable()
    {
        // FHIR clients fetch /metadata BEFORE they hold a token, to discover the interactions and where to
        // authenticate. The 18.B3 edge jwt plugin is global, so this one path carries an explicit exemption —
        // and it must stay exactly one path, not all of /fhir.
        var kong = Kong();
        kong.Should().Contain("/fhir/r4/metadata$", "the CapabilityStatement needs its own exempt route");
        var metadataRoute = Block(kong, "      - name: interop-metadata-route");
        metadataRoute.Should().Contain("enabled: false", "the edge jwt plugin must be disabled on that route");
    }

    [Fact]
    public void The_active_branch_header_is_allowed_and_exposed_by_cors()
    {
        // 18.C1 (W2). Missing from `headers` the preflight kills the request; missing from `exposed_headers`
        // the echo is invisible to JS. Both, or branch scoping is inert again.
        var kong = Kong();
        var cors = Block(kong, "  - name: cors");
        Regex.Match(cors, @"headers: \[([^\]]*)\]").Groups[1].Value
            .Should().Contain("X-Active-Branch", "the browser preflight rejects an unlisted request header");
        Regex.Match(cors, @"exposed_headers: \[([^\]]*)\]").Groups[1].Value
            .Should().Contain("X-Active-Branch", "the switcher reads the server's echo from the response");
    }

    /// <summary>The lines of a YAML block, from its header to the next line at the same or lower indent.
    /// Crude but sufficient, and it will not silently match a COMMENTED block — the whole point here.</summary>
    private static string Block(string yaml, string header)
    {
        var lines = yaml.Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == header.TrimEnd());
        start.Should().BeGreaterThanOrEqualTo(0, "block '{0}' must exist", header);
        var indent = header.Length - header.TrimStart().Length;
        var body = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { body.Add(line); continue; }
            var thisIndent = line.Length - line.TrimStart().Length;
            if (thisIndent <= indent) break;
            body.Add(line);
        }
        return string.Join('\n', body);
    }

    private static string Compose() => File.ReadAllText(Path.Combine(RepoRoot(), "infra", "compose", "compose.yaml"));
    private static string Kong() => File.ReadAllText(Path.Combine(RepoRoot(), "infra", "compose", "config", "kong.yml"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
