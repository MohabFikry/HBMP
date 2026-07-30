using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Admin.Tests;

/// <summary>
/// Phase 18.B3 (audit R2 S3) — every admin endpoint is gated by the FRAMEWORK, not only by a handler that
/// remembers to call <c>AdminGate</c> first.
///
/// The distinction is the whole finding. Before this, an unauthenticated POST to
/// <c>/api/v1/admin/role-bindings</c> was routed, model-bound and entered the handler; it was rejected on the
/// handler's first line, so the OUTCOME was right and nothing looked broken. But the control lived in a
/// convention — twenty-plus handlers each opening with the same three lines — and a convention has no failure
/// mode that anyone notices. The endpoint added next week without those lines is a public admin API, and the
/// only thing that would have caught it is a reviewer's memory.
///
/// So this test asserts the ROUTE TABLE, not a request. Route metadata is the one place that cannot be
/// satisfied by a handler being careful, and it covers endpoints that do not exist yet: add an ungated route
/// under /api/v1/admin and this goes red on the next run, whatever the handler does.
/// </summary>
public class AdminEndpointAuthorizationTests : IClassFixture<AdminEndpointAuthorizationTests.Host>
{
    private readonly Host _host;
    public AdminEndpointAuthorizationTests(Host host) => _host = host;

    /// <summary>Boots the real admin app just far enough to read its endpoint table. No DB is touched — the
    /// route table is built at startup from the Map* calls, before any handler runs.</summary>
    public sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Admin"] = "Host=localhost;Port=1;Database=x;Username=x;Password=x",
                ["Events:UseInMemoryOutbox"] = "true",
            }));
        }

        public IReadOnlyList<RouteEndpoint> Endpoints() =>
        [.. Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
    }

    private static string Path(RouteEndpoint e) => "/" + e.RoutePattern.RawText?.TrimStart('/');

    /// <summary>Endpoints deliberately reachable without an admin scope, each with the reason. The list is
    /// asserted for staleness below, so it cannot quietly absorb a new one.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["/health/live"] = "liveness probe — no principal exists, and a gated probe cannot report a dead service",
        ["/health/ready"] = "readiness probe — kubelet carries no bearer token, so a gated probe reports a healthy pod as broken forever",
        ["/metrics"] = "Prometheus scrape, in-cluster only; never routed through Kong",
    };

    [Fact]
    public void Every_admin_route_requires_authorization_at_the_framework()
    {
        var ungated = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/api/v1/admin", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(Path).Distinct().ToList();

        ungated.Should().BeEmpty(
            "an admin route with no authorization metadata is reachable by an anonymous caller as far as the " +
            "pipeline is concerned:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, ungated));
    }

    /// <summary>Admin routes that are authenticated but carry no admin scope, with the reason. Not a
    /// weakening: each is SELF-scoped — it answers only about the caller, from the caller's own token.</summary>
    private static readonly Dictionary<string, string> SelfScoped = new(StringComparer.Ordinal)
    {
        ["/api/v1/admin/break-glass/active"] =
            "16.6 (H5) — every service's break-glass provider calls this with the CALLER's token to discover " +
            "that caller's own active grants. Requiring admin:read would break elevation for the doctors and " +
            "nurses the mechanism exists for, and it discloses nothing they do not already hold.",
    };

    [Fact]
    public void Every_admin_route_names_an_admin_scope_policy()
    {
        // .RequireAuthorization() with no policy would satisfy the test above while letting ANY authenticated
        // staff member — a lab technician, a pharmacist — reach the role-binding surface.
        var weak = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/api/v1/admin", StringComparison.Ordinal))
            .Where(e => !SelfScoped.ContainsKey(Path(e)))
            .Where(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => a.Policy is { } p && p.Contains("admin:", StringComparison.Ordinal)))
            .Select(Path).Distinct().ToList();

        weak.Should().BeEmpty(
            "these admin routes authenticate but do not require an admin scope:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, weak));
    }

    [Fact]
    public void Mutating_admin_routes_require_the_write_scope()
    {
        // admin:read must not be enough to grant a role, de-provision a user or rewrite session policy. The
        // read/write split is the difference between "can see who has access" and "can give it".
        var readOnly = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/api/v1/admin", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is { } m
                        && m.HttpMethods.Any(h => h is "POST" or "PUT" or "PATCH" or "DELETE"))
            // Break-glass carries its own scope: requesting emergency access is not an admin write, and
            // demanding admin:write would put it out of reach of the clinicians it exists for.
            .Where(e => !Path(e).StartsWith("/api/v1/admin/break-glass", StringComparison.Ordinal))
            .Where(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => a.Policy is { } p && p.EndsWith("admin:write", StringComparison.Ordinal)))
            .Select(Path).Distinct().ToList();

        readOnly.Should().BeEmpty(
            "these mutating admin routes are reachable with admin:read alone:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, readOnly));
    }

    [Fact]
    public void Break_glass_lifecycle_routes_require_the_break_glass_scope()
    {
        var lifecycle = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/api/v1/admin/break-glass", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .ToList();

        lifecycle.Should().NotBeEmpty("the break-glass lifecycle endpoints must exist to be checked");
        foreach (var e in lifecycle)
            e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => a.Policy?.EndsWith("admin:break-glass", StringComparison.Ordinal) == true)
                .Should().BeTrue("{0} moves an emergency PHI grant", Path(e));
    }

    [Fact]
    public void Only_the_declared_exemptions_are_reachable_anonymously()
    {
        var anonymous = _host.Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(Path).Distinct().Order(StringComparer.Ordinal).ToList();

        anonymous.Should().BeSubsetOf(Exempt.Keys,
            "an endpoint outside the declared exemptions is anonymously reachable across the whole service");

        // Staleness: an exemption for a route that no longer exists hides the next one added under that path.
        var all = _host.Endpoints().Select(Path).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, reason) in Exempt)
            all.Should().Contain(path, "'{0}' is exempted ({1}) but no such route exists", path, reason);
        foreach (var (path, reason) in SelfScoped)
            all.Should().Contain(path, "'{0}' is declared self-scoped ({1}) but no such route exists", path, reason);
    }
}
