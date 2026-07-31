using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Authz;
using Mersal.Claims.Infrastructure;
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
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>
/// Phase 24 Gate 3 — the claims endpoints, hosted.
///
/// <para><b>Why this exists.</b> claims-service had 25 test files and 0.0% Api coverage: every one of them
/// called the SERVICE (AdjudicationService, DecisionService, ClaimIntakeExecutor) and none of them went
/// through an endpoint. The rules that live only in the endpoint layer — who may call it, what a provider
/// user is allowed to see, whether the programme gate applies, which fields reach the wire — were therefore
/// unproven on the money path, and a change to any of them would have failed no test. The domain logic
/// underneath is well covered; that is exactly what made the gap easy to miss.</para>
///
/// <para>Composition mirrors callcentre's <c>CallCentreFactory</c>, the house pattern: JwtBearer is swapped
/// for a header-driven scheme so a test can be a specific role, the DbContext points at the env-gated live
/// Postgres, the outbox is in-memory so a test can read what was published, and the contract tariff comes
/// from <see cref="FixedTariff"/> rather than an HTTP call to provider-service.</para>
/// </summary>
public sealed class ClaimsApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");

    /// <summary>The contract price every intake resolves to. Null ⇒ NO_TARIFF + manual review.</summary>
    public decimal? Tariff { get; set; } = 150m;

    public InMemoryOutbox Outbox { get; private set; } = default!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Claims"] = Db,
                ["Events:UseInMemoryOutbox"] = "true",
            }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(ClaimsTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, ClaimsTestAuth>(ClaimsTestAuth.SchemeName, _ => { });
            // provider-service is not running under test, and a tariff that arrived over HTTP would make
            // every price assertion depend on a sibling's fixtures.
            services.RemoveAll<IContractTariffProvider>();
            services.AddScoped<IContractTariffProvider>(_ => new FixedTariff(Tariff));
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    /// <summary>A fresh tenant per factory, so a leftover row from a failed run cannot make the next one
    /// pass (or fail) for a reason that has nothing to do with the code under test.</summary>
    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>A claims officer: may ingest, read, adjudicate, review and decide.</summary>
    public HttpClient OfficerClient(string? sub = null) => As(
        sub ?? ClaimsTestAuth.OfficerSub, "claims_officer",
        "claims:read claims:ingest claims:adjudicate claims:review claims:decide claims:batch claims:adjust claims:appeal");

    /// <summary>A senior reviewer: the dual-control counterpart, plus export.</summary>
    public HttpClient ReviewerClient(string? sub = null) => As(
        sub ?? ClaimsTestAuth.ReviewerSub, "claims_reviewer",
        "claims:read claims:review claims:decide claims:export claims:settle claims:reconcile");

    /// <summary>
    /// MERSAL STAFF carrying a provider id — a claims officer affiliated with one provider. Deliberately not
    /// a provider portal user: this caller is authorized by the tenant-wide <c>claim:read</c> rule, so it is
    /// the endpoint's own isolation check that has to hold them to their provider, not the ABAC condition.
    /// The two paths refuse a cross-provider read for different reasons and both are tested.
    /// </summary>
    public HttpClient ProviderScopedClient(Guid providerId)
    {
        var c = As(ClaimsTestAuth.ProviderSub, "claims_officer", "claims:read");
        c.DefaultRequestHeaders.Add("X-Test-Provider", providerId.ToString());
        return c;
    }

    /// <summary>A genuine provider portal user: provider_admin, isolated by ABAC provider-ownership to the
    /// claims, submissions and batches of its own provider (11-permission-matrix §3.4).</summary>
    public HttpClient ProviderAdminClient(Guid providerId)
    {
        var c = As(ClaimsTestAuth.ProviderSub, "provider_admin", "claims:read claims:submit claims:appeal");
        c.DefaultRequestHeaders.Add("X-Test-Provider", providerId.ToString());
        return c;
    }

    /// <summary>Finance: reads and exports, and must never reach a clinical field or a decision action.</summary>
    public HttpClient FinanceClient() => As(ClaimsTestAuth.FinanceSub, "finance", "claims:read claims:export");

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

    /// <summary>Remove everything this factory's tenant wrote. claim_decision is append-only by trigger, so
    /// the session lifts user triggers for the delete — the same shape DecisionIntegrationTests uses.
    /// Submissions and batches are deleted too: a provider submission creates its own rows and a batch is not
    /// reachable from claim, so deleting claims alone would leave both behind for every run.</summary>
    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM claims.claim_decision WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_submission_line WHERE submission_id IN " +
            "  (SELECT submission_id FROM claims.claim_submission WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim_submission WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_batch_item WHERE batch_id IN " +
            "  (SELECT batch_id FROM claims.claim_batch WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim_batch WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", Tenant);
    }

    public static DbContextOptionsBuilder<ClaimsDbContext> DbOptions() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention();

    public static ClaimsDbContext Ctx() => new(DbOptions().Options);
}

/// <summary>Builds a principal from X-Test-* headers. Same shape as callcentre's, including the programme
/// claim: every existing tenant was backfilled ON (migration 0009/0015), so the harness mirrors that by
/// default and <c>X-Test-Features</c> with an empty value asserts the gate REFUSES.</summary>
public sealed class ClaimsTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string OfficerSub = "11111111-1111-1111-1111-111111111111";
    public const string ReviewerSub = "22222222-2222-2222-2222-222222222222";
    public const string ProviderSub = "33333333-3333-3333-3333-333333333333";
    public const string FinanceSub = "44444444-4444-4444-4444-444444444444";

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
        if (Request.Headers.TryGetValue("X-Test-Provider", out var provider)) claims.Add(new Claim("provider_id", provider.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));

        if (Request.Headers.TryGetValue("X-Test-Features", out var features))
        {
            foreach (var f in features.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("features", f));
        }
        else
        {
            claims.Add(new Claim("features", ProgramFeatures.Claims));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
