using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.BenefitPricing;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
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

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 24 Gate 3 — the entitlement endpoints, hosted.
///
/// <para><b>Why this exists.</b> policy-service owns what every member is entitled to, and its Api layer —
/// 3,558 lines, the largest in the platform — measured 5.8%. The store and command suites next to this file
/// are thorough, and all of them call <c>MembershipCommands</c> or a DbContext directly. The endpoint layer
/// is where the authoring/administering separation is enforced (policy:admin authors a product, policy:write
/// administers a member against it), where a Draft plan version is refused, where a plan's window is checked
/// against its policy's, and where an unparseable eligibility rule is refused rather than read as "no
/// restriction". None of that had a test.</para>
///
/// <para>Every sibling seam is faked. policy-service reaches patient, provider, admin, document, emr,
/// approvals and claims over HTTP; a test that needed all seven running would never be written, which is a
/// large part of why this layer had no tests.</para>
/// </summary>
public sealed class PolicyApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");

    /// <summary>What patient-service says about the beneficiary. "Active" enrols; anything else is refused.</summary>
    public string BeneficiaryStatus { get; set; } = "Active";

    public InMemoryOutbox Outbox { get; private set; } = default!;

    /// <summary>A fresh tenant per factory. policy's tables are tenant-scoped, so this is also the cleanup key.</summary>
    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Policy"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(PolicyTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, PolicyTestAuth>(PolicyTestAuth.SchemeName, _ => { });

            s.RemoveAll<IBeneficiaryStatusProbe>();
            s.AddSingleton<IBeneficiaryStatusProbe>(new FakeStatusProbe(this));
            s.RemoveAll<INetworkTierCatalog>();
            s.AddSingleton<INetworkTierCatalog>(new FakeTierCatalog());
            s.RemoveAll<IPayerDirectory>();
            s.AddSingleton<IPayerDirectory>(new AllPayers());
            s.RemoveAll<IBranchDirectory>();
            s.AddSingleton<IBranchDirectory>(new AllBranches());

            // Timer-driven work writes to the tables the assertions read.
            s.RemoveAll<IHostedService>();
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    /// <summary>Authors the benefit PRODUCT: payers, plans, versions, policies. policy:admin.</summary>
    public HttpClient ProductAdminClient() =>
        As(PolicyTestAuth.AdminSub, "policy_admin", "policy:admin policy:read policy:write");

    /// <summary>Administers MEMBERS against a product: enrol, terminate, reinstate, move. policy:write, and
    /// deliberately NOT policy:admin — the separation this service's policy set is built around.</summary>
    public HttpClient MemberAdminClient() =>
        As(PolicyTestAuth.MemberAdminSub, "beneficiary_mgmt", "policy:write policy:read");

    /// <summary>Reads the configuration and may change nothing.</summary>
    public HttpClient ReaderClient() => As(PolicyTestAuth.ReaderSub, "claims_officer", "policy:read");

    public HttpClient As(string sub, string role, string scopes)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        c.DefaultRequestHeaders.Add("X-Test-Role", role);
        c.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        return c;
    }

    /// <summary>Children first, and the enrolment event log needs its append-only trigger lifted for the
    /// session — the same shape the policy store suites use.</summary>
    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM policy.coverage_limit WHERE coverage_id IN " +
            "  (SELECT coverage_id FROM policy.coverage WHERE tenant_id = {0}); " +
            "DELETE FROM policy.coverage WHERE tenant_id = {0}; " +
            "DELETE FROM policy.enrollment_event WHERE tenant_id = {0}; " +
            "DELETE FROM policy.enrollment WHERE tenant_id = {0}; " +
            "DELETE FROM policy.member_group WHERE tenant_id = {0}; " +
            "DELETE FROM policy.policy_plan WHERE tenant_id = {0}; " +
            "DELETE FROM policy.policy WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", Tenant);
    }

    public static PolicyDbContext Ctx() =>
        new(new DbContextOptionsBuilder<PolicyDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

internal sealed class FakeStatusProbe(PolicyApiFactory f) : IBeneficiaryStatusProbe
{
    public Task<string?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult<string?>(f.BeneficiaryStatus);
}

/// <summary>One active tier, so plan-version validation has a priced grid to check against without
/// provider-service running.</summary>
internal sealed class FakeTierCatalog : INetworkTierCatalog
{
    public static readonly Guid TierId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    public Task<IReadOnlyList<NetworkTierRef>> ActiveTiersAsync(string? bearerToken, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NetworkTierRef>>([new NetworkTierRef(TierId, "TIER-A")]);
}

/// <summary>Not payer-restricted — the common case. Payer scoping has its own suite.</summary>
internal sealed class AllPayers : IPayerDirectory
{
    public Task<PermittedPayers> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
        => Task.FromResult(PermittedPayers.Unrestricted);
}

internal sealed class AllBranches : IBranchDirectory
{
    public Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
        => Task.FromResult(PermittedBranches.None);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class PolicyTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string AdminSub = "11111111-1111-1111-1111-111111111111";
    public const string MemberAdminSub = "22222222-2222-2222-2222-222222222222";
    public const string ReaderSub = "33333333-3333-3333-3333-333333333333";

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

