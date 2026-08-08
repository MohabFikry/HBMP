using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Profile.Domain;
using Mersal.Profile.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mersal.Profile.Tests;

/// <summary>
/// Phase 24 Gate 3 — the patient profile, over HTTP.
///
/// <para>profile-service's Api layer measured 2.4%. The composer and the field projection are thoroughly
/// tested below HTTP (SerializedPayloadTests, CompositionTests); what those cannot reach is the gate the
/// endpoint asks BEFORE composing anything — the call-centre verification check. An agent who has not
/// verified the person on the line is refused the profile outright. It is CONSUMED here rather than
/// re-implemented, so a wiring mistake would leave the check present in callcentre-service and absent from
/// the screen that actually shows the record.</para>
/// </summary>
public class ProfileEndpointTests
{
    /// <summary>
    /// The call-centre gate. An agent reaches the profile only through a verified interaction: with no
    /// interaction id, and with one the gate rejects, the answer is 403 and nothing is composed.
    /// </summary>
    [Fact]
    public async Task An_unverified_call_centre_agent_is_refused_the_profile()
    {
        await using var app = new ProfileApiFactory { Verified = false };
        using var agent = app.AgentClient();
        var beneficiaryId = Guid.NewGuid();

        var noInteraction = await agent.GetAsync(
            new Uri($"/api/v1/patients/{beneficiaryId}/profile", UriKind.Relative));
        noInteraction.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "{0}", await noInteraction.Content.ReadAsStringAsync());
        (await noInteraction.Content.ReadAsStringAsync()).Should().Contain("not-verified");

        var rejected = await agent.GetAsync(new Uri(
            $"/api/v1/patients/{beneficiaryId}/profile?interactionId={Guid.NewGuid()}", UriKind.Relative));
        rejected.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        app.Composed.Should().Be(0, "the gate is asked before anything is composed, so an unverified agent " +
                                    "never causes a cross-service read either");
    }

    /// <summary>The same agent, on a verified interaction, is served. Without this half the test above would
    /// pass on a service that refused everybody.</summary>
    [Fact]
    public async Task A_verified_call_centre_agent_is_served()
    {
        await using var app = new ProfileApiFactory { Verified = true };
        using var agent = app.AgentClient();

        var served = await agent.GetAsync(new Uri(
            $"/api/v1/patients/{Guid.NewGuid()}/profile?interactionId={Guid.NewGuid()}", UriKind.Relative));
        served.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await served.Content.ReadAsStringAsync());
        app.Composed.Should().Be(1);
    }

    /// <summary>The verification gate is an EXTRA condition on top of authorization, never a replacement:
    /// a caller with no profile scope is refused, and an anonymous one reaches nothing.</summary>
    [Fact]
    public async Task Authorization_still_applies_on_top_of_the_verification_gate()
    {
        await using var app = new ProfileApiFactory { Verified = true };

        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync(new Uri($"/api/v1/patients/{Guid.NewGuid()}/profile", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var noScope = app.As("44444444-4444-4444-4444-444444444444", "reception", "orders:read");
        (await noScope.GetAsync(new Uri($"/api/v1/patients/{Guid.NewGuid()}/profile", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The export is a DIFFERENT act from the read and carries its watermark on the PAYLOAD — an export that
    /// can be printed without it leaves the building unattributed, and a watermark added by the client is one
    /// the client can leave out.
    /// </summary>
    [Fact]
    public async Task The_export_summary_carries_its_watermark_in_the_payload()
    {
        await using var app = new ProfileApiFactory { Verified = true };
        using var doctor = app.DoctorClient();

        var r = await doctor.GetAsync(new Uri(
            $"/api/v1/patients/{Guid.NewGuid()}/profile/summary?purpose=continuity-of-care", UriKind.Relative));
        r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var watermark = doc.RootElement.GetProperty("watermark");
        watermark.GetProperty("viewerSubject").GetString().Should().Be(ProfileApiFactory.DoctorSub);
        watermark.GetProperty("viewerRoles").GetString().Should().Contain("doctor");
        watermark.GetProperty("purpose").GetString().Should().Be("continuity-of-care");
    }
}

/// <summary>
/// Hosts the real profile endpoints.
///
/// <para>Three things are replaced, and each for a reason worth writing down. The FACT RESOLVER and the
/// VERIFICATION GATE are the two siblings the endpoint consults before serving — faked so the test decides
/// the answer rather than callcentre-service's fixtures. The SECTION PROVIDERS are removed: each calls a
/// different service over HTTP, and with none registered the composer reports every section Unavailable,
/// which is what this suite wants — CompositionTests already proves what the composer does with real ones.
/// And the AUDIT SINK: profile-service publishes audit DIRECTLY to the broker rather than through an outbox
/// (AddHbmpDirectAuditSink), so with no broker running every request — including the 403 the gate produces —
/// blocked for the connection timeout and surfaced as a 500. That is why this suite is a test-host swap and
/// not just a client.</para>
/// </summary>
public sealed class ProfileApiFactory : WebApplicationFactory<Program>
{
    public const string AgentSub = "11111111-1111-1111-1111-111111111111";
    public const string DoctorSub = "22222222-2222-2222-2222-222222222222";

    /// <summary>What callcentre-service says about the interaction on the line.</summary>
    public bool Verified { get; set; }

    /// <summary>How many times the profile was actually composed. Zero proves a refusal happened BEFORE any
    /// cross-service read, which is the difference between a gate and a filter.</summary>
    public int Composed => _facts.Calls;

    private readonly CountingFacts _facts = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        // UseSetting, not ConfigureAppConfiguration: profile-service reads Auth:Authority off
        // builder.Configuration while Program is still executing, before the host-level configuration
        // callback would have been applied.
        builder.UseSetting("Auth:Authority", "https://identity.test");
        builder.UseSetting("Auth:Audience", "hbmp");
        builder.UseSetting("Events:UseInMemoryOutbox", "true");
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(ProfileTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, ProfileTestAuth>(ProfileTestAuth.SchemeName, _ => { });
            s.RemoveAll<IProfileFactResolver>();
            s.AddSingleton<IProfileFactResolver>(_facts);
            s.RemoveAll<ICallVerificationGate>();
            s.AddSingleton<ICallVerificationGate>(new FakeVerification(this));
            // One fixed-payload provider per section key. Registering NONE looked simpler and was wrong: the
            // composer expects a provider for every key it is asked to serve and threw, which surfaced as a
            // 500 on the served path while the refusal path passed — the shape that makes a gate test read
            // as working while proving only the refusal.
            s.RemoveAll<ISectionProvider>();
            foreach (var key in ProfileSections.All)
                s.AddSingleton<ISectionProvider>(new FakeProvider(key, new { key }));
            s.Replace(ServiceDescriptor.Singleton<IAuditOutbox>(new NullAuditOutbox()));
            s.RemoveAll<IHostedService>();
        });
    }

    public HttpClient AgentClient() => As(AgentSub, "call_center", "profile:read profile:export");
    public HttpClient DoctorClient() => As(DoctorSub, "doctor", "profile:read profile:export");

    public HttpClient As(string sub, string role, string scopes)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        c.DefaultRequestHeaders.Add("X-Test-Role", role);
        c.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "t-profile");
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        // A real Authorization header, even though the test scheme reads none of it: the composer refuses to
        // run without a caller bearer, because the profile composes under the CALLER'S token and a
        // service-account fallback does not exist by design (design 39 §7.2). A harness that omitted it would
        // report a 500 for the one property this service is built around.
        c.DefaultRequestHeaders.Add("Authorization", "Bearer test-caller-token");
        return c;
    }

    private sealed class CountingFacts : IProfileFactResolver
    {
        public int Calls { get; private set; }

        public Task<ProfileContext> ResolveAsync(
            HbmpPrincipal principal, Guid beneficiaryId, CallerCredentials caller, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(principal);
            Calls++;
            return Task.FromResult(new ProfileContext { Roles = principal.Roles.ToHashSet(StringComparer.Ordinal) });
        }
    }

    private sealed class FakeVerification(ProfileApiFactory f) : ICallVerificationGate
    {
        public Task<bool> IsVerifiedAsync(Guid interactionId, Guid beneficiaryId, CallerCredentials caller,
            CancellationToken ct) => Task.FromResult(f.Verified);
    }

    /// <summary>Swallows the audit emit. What is audited on a profile read has its own suite; here the
    /// direct-to-broker sink would only be a connection timeout.</summary>
    private sealed class NullAuditOutbox : IAuditOutbox
    {
        public ValueTask EnqueueAsync(AuditEvent auditEvent, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class ProfileTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("sub", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Role", out var role))
            foreach (var r in role.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("role", r));
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) claims.Add(new Claim("tenant_id", tenant.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

