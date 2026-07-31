using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Authz;
using Mersal.Case.Domain;
using Mersal.Case.Infrastructure;
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

namespace Mersal.Case.Tests;

/// <summary>
/// Phase 24 Gate 3 — the case endpoints, hosted.
///
/// <para><b>Why this exists.</b> case-service's Api layer measured 0.0% over 614 lines, and its whole access
/// model lives there: an ASSIGNMENT is the ABAC anchor, so assigning grants a case manager access to a case
/// and unassigning revokes it. Nothing tested either. The 360 view is the other half — the one screen that
/// deliberately crosses service boundaries to assemble a beneficiary's picture, and the one whose disclosure
/// therefore has to be gated and audited.</para>
/// </summary>
public sealed class CaseApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("CASE_TEST_DB");

    /// <summary>What profile-service returns for the 360. Null models "not disclosable on this call", which
    /// the endpoint reports as 404 rather than leaking that the case exists.</summary>
    public Beneficiary360? Profile { get; set; }

    public InMemoryOutbox Outbox { get; private set; } = default!;
    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Case"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(CaseTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, CaseTestAuth>(CaseTestAuth.SchemeName, _ => { });
            s.RemoveAll<IBeneficiary360Assembler>();
            s.AddSingleton<IBeneficiary360Assembler>(new FakeAssembler(this));
            s.RemoveAll<IHostedService>();
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    /// <summary>A case manager: opens cases, works them, and sees only what they are assigned to.</summary>
    public HttpClient ManagerClient(string? sub = null) => As(
        sub ?? CaseTestAuth.ManagerSub, "case_manager",
        "case:read case:read-list case:read-360 case:write case:open");

    /// <summary>A supervisor: assigns and unassigns — the action that grants and revokes the access above.</summary>
    public HttpClient SupervisorClient() => As(
        CaseTestAuth.SupervisorSub, "manager", "case:manage case:read case:read-list case:write");

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

    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"case\".escalation WHERE case_id IN (SELECT case_id FROM \"case\".case_file WHERE tenant_id = {0}); " +
            "DELETE FROM \"case\".coordination_task WHERE case_id IN (SELECT case_id FROM \"case\".case_file WHERE tenant_id = {0}); " +
            "DELETE FROM \"case\".case_assignment WHERE case_id IN (SELECT case_id FROM \"case\".case_file WHERE tenant_id = {0}); " +
            "DELETE FROM \"case\".case_file WHERE tenant_id = {0};", Tenant);
    }

    public static CaseDbContext Ctx() =>
        new(new DbContextOptionsBuilder<CaseDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

internal sealed class FakeAssembler(CaseApiFactory f) : IBeneficiary360Assembler
{
    public Task<Beneficiary360?> AssembleAsync(CaseFile @case, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(f.Profile);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class CaseTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string ManagerSub = "11111111-1111-1111-1111-111111111111";
    public const string OtherManagerSub = "44444444-4444-4444-4444-444444444444";
    public const string SupervisorSub = "22222222-2222-2222-2222-222222222222";

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

        if (Request.Headers.TryGetValue("X-Test-Features", out var features))
        {
            foreach (var f in features.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("features", f));
        }
        else
        {
            claims.Add(new Claim("features", ProgramFeatures.CaseManagement));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
