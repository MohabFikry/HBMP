using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Mersal.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mersal.Hello.Tests;

/// <summary>
/// End-to-end vertical-slice test (phase-0 §0.5 acceptance): an authenticated MFA request routes
/// through authorization, performs one audited action, and publishes a domain event via the outbox;
/// an unauthenticated request is rejected. Runs offline (no Keycloak) via a test auth scheme.
/// </summary>
public class HelloSliceTests(HelloFactory factory) : IClassFixture<HelloFactory>
{
    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/v1/hello", UriKind.Relative));
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        var client = factory.CreateClient();
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Authorized_mfa_request_performs_audited_action_and_publishes_event()
    {
        factory.Outbox.Clear();
        var client = factory.CreateAuthenticatedClient(mfa: true, scope: "hello:read", tenant: "t0");

        var resp = await client.GetAsync(new Uri("/api/v1/hello", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var msgs = factory.Outbox.AllMessages;
        // Both the audit event and the domain event flowed through the transactional outbox.
        msgs.Should().Contain(m => m.EventType == "AuditEventRecorded");
        msgs.Should().Contain(m => m.EventType == "GreetingViewed");
    }

    [Fact]
    public async Task Token_without_mfa_is_rejected_for_the_protected_scope()
    {
        var client = factory.CreateAuthenticatedClient(mfa: false, scope: "hello:read", tenant: "t0");
        var resp = await client.GetAsync(new Uri("/api/v1/hello", UriKind.Relative));
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}

/// <summary>Factory that swaps JwtBearer for a controllable test auth scheme and exposes the outbox.</summary>
public sealed class HelloFactory : WebApplicationFactory<Program>
{
    public InMemoryOutbox Outbox { get; private set; } = default!;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public HttpClient CreateAuthenticatedClient(bool mfa, string scope, string tenant)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", "user-1");
        client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        if (mfa) client.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        return client;
    }

    protected override void ConfigureClient(HttpClient client) => base.ConfigureClient(client);

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        // Capture the singleton outbox for assertions.
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }
}

/// <summary>Test authentication handler: builds a principal from X-Test-* headers.</summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            return Task.FromResult(AuthenticateResult.NoResult()); // unauthenticated

        var claims = new List<Claim> { new("sub", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) claims.Add(new Claim("tenant_id", tenant.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
