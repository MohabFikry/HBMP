using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Approvals.Api;
using Mersal.Approvals.Infrastructure;
using Mersal.Authz;
using Mersal.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mersal.Approvals.Tests;

/// <summary>
/// Phase 24 Gate 3 — the authorization decision endpoints, hosted.
///
/// <para><b>Why this exists.</b> approvals-service's Api layer measured 2.6% over 761 lines. Its decision
/// RULES are well covered (DecisionRulesTests, DecisionConcurrencyTests) and none of that runs through the
/// endpoint, so what was untested is precisely the part that decides who may decide: the separation between
/// reviewing and deciding, the medical-director-only break-glass paths, the mandatory rejection reason, and
/// the partial-approval scope check that stops "partially approved" being used to approve everything.</para>
/// </summary>
public sealed class ApprovalsApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("APPROVALS_TEST_DB");

    public InMemoryOutbox Outbox { get; private set; } = default!;
    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Approvals"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(ApprovalsTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, ApprovalsTestAuth>(ApprovalsTestAuth.SchemeName, _ => { });
            s.RemoveAll<IClinicalContextProvider>();
            s.AddSingleton<IClinicalContextProvider>(new NoClinicalContext());
            // The callback into pharmacy / orders. Stubbed to succeed so decision tests exercise the DECISION
            // path; the refusal-on-failure behaviour is proved directly in ValidityExtensionTests.
            s.RemoveAll<IValidityExtensionApplier>();
            s.AddSingleton(Applier);
            s.RemoveAll<IHostedService>();
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    /// <summary>The approval team: reviews and decides, and holds no break-glass power.</summary>
    public HttpClient ReviewerClient(string? sub = null) => As(
        sub ?? ApprovalsTestAuth.ReviewerSub, "medical_approval",
        "auth:read auth:list auth:review auth:decide");

    /// <summary>The medical director: the only role the emergency, override and manual paths admit.</summary>
    public HttpClient DirectorClient() => As(
        ApprovalsTestAuth.DirectorSub, "medical_director",
        "auth:read auth:list auth:review auth:decide auth:emergency auth:override auth:manual");

    /// <summary>A pharmacist at a bound pharmacy — may ASK for an extension and nothing else.</summary>
    public HttpClient PharmacistClient(string? providerId = null)
    {
        var c = As("22222222-2222-2222-2222-222222222222", "pharmacist", "auth:request-extension");
        c.DefaultRequestHeaders.Add("X-Test-Provider", providerId ?? "44444444-4444-4444-4444-444444444444");
        return c;
    }

    /// <summary>A lab technician at a bound lab — may ASK about a different examination and nothing else.</summary>
    public HttpClient TechnicianClient(string? providerId = null)
    {
        var c = As("55555555-5555-5555-5555-555555555555", "lab_tech", "auth:request-substitution");
        c.DefaultRequestHeaders.Add("X-Test-Provider", providerId ?? "44444444-4444-4444-4444-444444444444");
        return c;
    }

    /// <summary>The callback into pharmacy / orders. Succeeds by default so decision tests exercise the
    /// DECISION path; a test that is about the callback failing substitutes its own.</summary>
    public IValidityExtensionApplier Applier { get; init; } = new NoopValidityExtensionApplier();

    public HttpClient As(string sub, string role, string scopes, string? features = null)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        c.DefaultRequestHeaders.Add("X-Test-Role", role);
        c.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        if (features is not null) c.DefaultRequestHeaders.Add("X-Test-Features", features);
        return c;
    }

    /// <summary>The decision ledger is append-only by trigger, so the session lifts user triggers for the
    /// delete — the same shape the approvals integration suite uses.</summary>
    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM approvals.authorization_decision WHERE authorization_id IN " +
            "  (SELECT authorization_id FROM approvals.authorization WHERE tenant_id = {0}); " +
            "DELETE FROM approvals.processed_request WHERE authorization_id IN " +
            "  (SELECT authorization_id FROM approvals.authorization WHERE tenant_id = {0}); " +
            "DELETE FROM approvals.authorization_item WHERE tenant_id = {0}; " +
            "DELETE FROM approvals.authorization WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", Tenant);
    }

    public static ApprovalsDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ApprovalsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

/// <summary>emr is not running. The review screen's clinical context has its own suite; here its absence must
/// not decide the outcome of an authorization test.</summary>
internal sealed class NoClinicalContext : IClinicalContextProvider
{
    public Task<ClinicalContext?> GetAsync(Guid beneficiaryId, string? sourceRef, string? bearerToken,
        CancellationToken ct = default) => Task.FromResult<ClinicalContext?>(null);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class ApprovalsTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string ReviewerSub = "11111111-1111-1111-1111-111111111111";
    public const string SecondReviewerSub = "33333333-3333-3333-3333-333333333333";
    public const string DirectorSub = "22222222-2222-2222-2222-222222222222";

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
        // Provider-scoped roles (pharmacist, lab_tech, imaging_tech) carry theirs on the membership; a
        // validity-extension request is raised on behalf of that provider, so the tests need one.
        if (Request.Headers.TryGetValue("X-Test-Provider", out var provider)) claims.Add(new Claim("provider_id", provider.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));

        if (Request.Headers.TryGetValue("X-Test-Features", out var features))
        {
            foreach (var f in features.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("features", f));
        }
        else
        {
            claims.Add(new Claim("features", ProgramFeatures.Approvals));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
