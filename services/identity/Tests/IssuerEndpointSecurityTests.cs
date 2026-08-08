using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 18.B3 (audit R2 S3/S4/S7/S9) — the issuer's own attack surface, asserted from the route table and the
/// middleware pipeline rather than from a handler's good behaviour.
///
/// identity-service is the one service where a mistake is not contained: it holds the password store, mints
/// every token, and enrols the second factor that gates every admin scope on the platform. Four things were
/// missing at once — no framework authorization on <c>/identity/admin</c>, antiforgery disabled on all three
/// credential forms, no transport security, and no rate limit on the endpoints where a secret is guessed.
/// </summary>
public class IssuerEndpointSecurityTests : IClassFixture<IssuerEndpointSecurityTests.Host>
{
    private readonly Host _host;
    public IssuerEndpointSecurityTests(Host host) => _host = host;

    public sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Identity"] = "Host=localhost;Port=1;Database=x;Username=x;Password=x",
                ["Events:UseInMemoryOutbox"] = "true",
                ["Issuer:SeedDemoUsers"] = "false",
                ["Issuer:ServiceClientSecret"] = "test-only-not-a-real-secret",
            }));
            // The route table is built from the Map* calls at startup and needs no database. The seeders
            // (ClientSeeder / UserSeeder) are IHostedService and DO hit one, so drop them rather than making
            // a pure route-metadata assertion depend on live Postgres.
            builder.ConfigureTestServices(services =>
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
        }

        public IReadOnlyList<RouteEndpoint> Endpoints() =>
        [.. Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

        public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();
    }

    private static string Path(RouteEndpoint e) => "/" + e.RoutePattern.RawText?.TrimStart('/');

    private static bool IsPost(RouteEndpoint e) =>
        e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true;

    // ---- S3: the admin surface + the catalog ------------------------------------------------------------

    [Fact]
    public void Every_identity_admin_route_requires_authorization_at_the_framework()
    {
        var ungated = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/identity/admin", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(Path).ToList();

        ungated.Should().BeEmpty(
            "these create users, set roles and reset passwords; they were reachable by an anonymous request " +
            "and stopped only by each handler remembering to call Guard on its first line:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, ungated));
    }

    [Fact]
    public void The_rbac_catalog_is_no_longer_anonymous()
    {
        // /identity/roles + /scopes + /effective-scopes hold no user data, but together they are the platform's
        // complete authorization map — which role to pivot to for admin:break-glass, obtainable without a token.
        foreach (var path in new[] { "/identity/roles", "/identity/scopes", "/identity/effective-scopes" })
        {
            var endpoint = _host.Endpoints().SingleOrDefault(e => Path(e) == path);
            endpoint.Should().NotBeNull("{0} must exist", path);
            endpoint!.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull("{0} must require a token", path);
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull("{0} must not be anonymous", path);
        }
    }

    [Fact]
    public void Only_the_health_probes_are_anonymous()
    {
        // The OIDC endpoints are NOT in this list: they are anonymous by protocol (that is how a caller gets a
        // token at all), but OpenIddict owns their authorization, not [AllowAnonymous].
        //
        // Both probes are anonymous because kubelet carries no bearer token: a gated liveness probe cannot
        // report a dead service, and a gated readiness probe never reports Ready, so the rollout hangs. They
        // are the ONLY additions this list should ever grow by without a security review.
        var anonymous = _host.Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Path).Distinct().ToList();

        anonymous.Should().BeEquivalentTo(["/health/live", "/health/ready"]);
    }

    // ---- S4: CSRF ----------------------------------------------------------------------------------------

    [Fact]
    public void No_rendered_form_post_disables_antiforgery()
    {
        // The sharpest case is POST /connect/enroll-2fa: it is authenticated by the session COOKIE, so a
        // cross-site form post from a page the victim visits enrols the ATTACKER's authenticator as the
        // victim's second factor — and the flow then stamps amr=otp, satisfying MFA for that account.
        var disabled = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/connect", StringComparison.Ordinal) && IsPost(e))
            .Where(e => e.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: false })
            .Select(Path).ToList();

        disabled.Should().BeEmpty(
            "a cross-site POST to these registers or replays a credential:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, disabled));
    }

    [Fact]
    public void The_three_rendered_forms_carry_a_token_field()
    {
        // The middleware only helps if the form actually posts a token; a validated endpoint whose form omits
        // the field is a 400 for every legitimate user, which is the failure mode that gets it disabled again.
        var antiforgery = _host.Resolve<IAntiforgery>();
        using var scope = _host.Services.CreateScope();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var field = Mersal.Identity.Api.Auth.AccountPages.AntiforgeryField(antiforgery, http);

        field.Should().Contain("type=\"hidden\"").And.Contain("__hbmp_csrf");
        Mersal.Identity.Api.Auth.AccountPages.LoginPage("en", null, field).Should().Contain("__hbmp_csrf");
    }

    // ---- S7 / S9: transport + rate limits ----------------------------------------------------------------

    [Fact]
    public void The_credential_endpoints_are_rate_limited()
    {
        // Identity's lockout stops per-ACCOUNT password guessing. It does nothing about password spraying
        // across many usernames, and nothing at all about a 6-digit TOTP code, which at Kong's global
        // 1200/min is brute-forcible inside the code's own validity window.
        foreach (var path in new[] { "/connect/login", "/connect/2fa", "/connect/enroll-2fa", "/connect/token" })
        {
            var post = _host.Endpoints().SingleOrDefault(e => Path(e) == path && IsPost(e));
            post.Should().NotBeNull("{0} must exist as a POST", path);
            post!.Metadata.Any(m => m.GetType().Name.Contains("RateLimiting", StringComparison.Ordinal))
                .Should().BeTrue("{0} accepts a guessable secret and must carry a rate-limit policy", path);
        }
    }

    [Fact]
    public void The_issuer_registers_transport_security()
    {
        // S7: identity-service was the only service without UseHbmpTransportSecurity — on the one host that
        // transmits passwords, TOTP codes and bearer tokens. The middleware is registered unconditionally and
        // enforces HSTS + redirect outside Development, so assert the registration, not the dev behaviour.
        var program = File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "services", "identity", "Api", "Program.cs"));
        program.Should().Contain("app.UseHbmpTransportSecurity();");
        program.IndexOf("app.UseHbmpTransportSecurity();", StringComparison.Ordinal)
            .Should().BeLessThan(program.IndexOf("app.UseExceptionHandler();", StringComparison.Ordinal),
                "it must be the FIRST middleware, so even a failing request is answered over TLS");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
