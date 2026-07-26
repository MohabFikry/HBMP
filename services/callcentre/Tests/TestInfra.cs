using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;
using Mersal.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mersal.CallCentre.Tests;

/// <summary>Web-host test factory for the real callcentre endpoints: swaps JwtBearer for a controllable test auth
/// scheme, points the DbContext at the live PG (env-gated <c>CALLCENTRE_TEST_DB</c>), and injects a fake
/// <see cref="IMemberDirectory"/> so the composition is deterministic (no live siblings needed).</summary>
public sealed class CallCentreFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("CALLCENTRE_TEST_DB");
    public FakeMemberDirectory Directory { get; } = new();
    public FakeAppointmentGateway Gateway { get; } = new();
    public FakeContactGateway Contacts { get; } = new();
    public InMemoryOutbox Outbox { get; private set; } = default!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CallCentre"] = Db }));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.RemoveAll<IMemberDirectory>();
            services.AddSingleton<IMemberDirectory>(Directory);
            services.RemoveAll<IAppointmentGateway>();
            services.AddSingleton<IAppointmentGateway>(Gateway);
            services.RemoveAll<IContactGateway>();
            services.AddSingleton<IContactGateway>(Contacts);
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    public HttpClient AgentClient()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", TestAuthHandler.AgentSub);
        c.DefaultRequestHeaders.Add("X-Test-Role", "call_center");
        c.DefaultRequestHeaders.Add("X-Test-Scope", "callcentre:interaction callcentre:verify callcentre:read callcentre:act");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "t-callcentre");
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        return c;
    }

    public HttpClient SupervisorClient()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", "22222222-2222-2222-2222-222222222222");
        c.DefaultRequestHeaders.Add("X-Test-Role", "call_center_supervisor");
        c.DefaultRequestHeaders.Add("X-Test-Scope", "callcentre:interaction callcentre:verify callcentre:read callcentre:act");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "t-callcentre");
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        return c;
    }
}

/// <summary>Deterministic member directory for endpoint tests — a search hit + a cross-branch 360.</summary>
public sealed class FakeMemberDirectory : IMemberDirectory
{
    public Guid BeneficiaryId { get; } = Guid.NewGuid();

    public Task<MemberSearchResult> SearchAsync(string query, string? bearer, CancellationToken ct = default) =>
        Task.FromResult(new MemberSearchResult(query, 1,
            [new MemberMatch(BeneficiaryId, "Amal Hassan", "MRS-M-1001", ["MemberNo", "DateOfBirth", "Phone"])]));

    public Task<Member360?> AssembleAsync(Guid beneficiaryId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<Member360?>(new Member360(
            new MemberIdentity(beneficiaryId, "MRS-M-1001", "Amal Hassan", "30-39", "Active", StatusCue.For("Active")),
            [new CoverageLine("Outpatient", 10000m, 7500m)],
            [new MemberContact(Guid.NewGuid(), "Phone", "+20100000000", true, "WhatsApp")],
            [
                new MemberAppointment(Guid.NewGuid(), "Consultation", "Scheduled", DateTimeOffset.UtcNow.AddDays(3), "Aswan", "Dr. Nour", "Cardiology", true, true),
                new MemberAppointment(Guid.NewGuid(), "Consultation", "Completed", DateTimeOffset.UtcNow.AddDays(-30), "Maadi", "Dr. Sami", "Dermatology", false, false),
            ],
            [new MemberReferral("REF-2026-000007", "Requested", "Endocrinology", DateTimeOffset.UtcNow)],
            []));
}

/// <summary>Deterministic emr gateway for endpoint tests — records what it received (headers, idempotency, If-Match)
/// and returns a successful appointment id, so the callcentre delegation + linkage can be asserted without emr.</summary>
public sealed class FakeAppointmentGateway : IAppointmentGateway
{
    internal static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);
    public Guid BookedAppointmentId { get; } = Guid.NewGuid();
    public string? LastIdempotencyKey { get; private set; }
    public string? LastIfMatch { get; private set; }
    public string? LastMethod { get; private set; }
    public string? LastBookAppointmentType { get; private set; }

    public Task<GatewayResult> SearchSlotsAsync(string qs, string? bearer, CancellationToken ct = default)
    {
        LastMethod = "slots";
        return Task.FromResult(new GatewayResult(200, "[]", null));
    }

    public Task<GatewayResult> BookAsync(object body, string? bearer, string? idem, CancellationToken ct = default)
    {
        LastMethod = "book"; LastIdempotencyKey = idem;
        using var doc = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(body, FakeAppointmentGateway.Web));
        LastBookAppointmentType = doc.RootElement.TryGetProperty("appointmentType", out var t) ? t.GetString() : null;
        return Task.FromResult(new GatewayResult(201, $"{{\"appointmentId\":\"{BookedAppointmentId}\"}}", BookedAppointmentId));
    }

    public Task<GatewayResult> RescheduleAsync(Guid id, object body, string? bearer, string? idem, string? ifMatch, CancellationToken ct = default)
    {
        LastMethod = "reschedule"; LastIdempotencyKey = idem; LastIfMatch = ifMatch;
        return Task.FromResult(new GatewayResult(200, $"{{\"appointmentId\":\"{id}\"}}", id));
    }

    public Task<GatewayResult> CancelAsync(Guid id, object body, string? bearer, string? idem, string? ifMatch, CancellationToken ct = default)
    {
        LastMethod = "cancel"; LastIdempotencyKey = idem; LastIfMatch = ifMatch;
        return Task.FromResult(new GatewayResult(200, $"{{\"appointmentId\":\"{id}\"}}", id));
    }
}

/// <summary>Deterministic patient-service contact gateway — records the last edit, returns success.</summary>
public sealed class FakeContactGateway : IContactGateway
{
    public string? LastKind { get; private set; }
    public string? LastValue { get; private set; }

    public Task<GatewayResult> UpdateContactAsync(Guid ben, Guid contactId, object body, string? bearer, CancellationToken ct = default)
    {
        Capture(body);
        return Task.FromResult(new GatewayResult(200, "{\"ok\":true}", null));
    }

    public Task<GatewayResult> AddContactAsync(Guid ben, object body, string? bearer, CancellationToken ct = default)
    {
        Capture(body);
        return Task.FromResult(new GatewayResult(201, "{\"ok\":true}", null));
    }

    private void Capture(object body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(body, FakeAppointmentGateway.Web));
        LastKind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
        LastValue = doc.RootElement.TryGetProperty("value", out var v) ? v.GetString() : null;
    }
}

/// <summary>Test auth handler: builds a principal from X-Test-* headers (sub / role / scope / tenant / mfa).</summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string AgentSub = "11111111-1111-1111-1111-111111111111";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("sub", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Role", out var role))
            foreach (var r in role.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)) claims.Add(new Claim("role", r));
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) claims.Add(new Claim("tenant_id", tenant.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
