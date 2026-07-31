using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Orders.Api;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Mersal.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace Mersal.Orders.Tests;

/// <summary>
/// Phase 24 Gate 3 — the orders endpoints, hosted.
///
/// <para><b>Why this exists.</b> INV-CONSUME-ATOMIC is the platform's headline invariant and orders-service
/// measured 1.3% Api coverage: the concurrency and idempotency proofs all called <c>ConsumeExecutor</c>
/// directly, so the ENDPOINT around it — the Idempotency-Key requirement, the provider-ownership and
/// lab/imaging capability gate, and the mapping from each executor outcome to its HTTP status — was
/// unproven. A consume endpoint that returned 200 on an over-consume would have failed no test.</para>
///
/// <para>Every sibling this service calls over HTTP is faked, because a test whose result depends on
/// masterdata-service's fixtures is testing masterdata-service.</para>
/// </summary>
public sealed class OrdersApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("ORDERS_TEST_DB");

    /// <summary>Whether the ordering doctor treats the beneficiary. False ⇒ the create gate refuses (403).</summary>
    public bool Treats { get; set; } = true;

    /// <summary>Whether masterdata recognises the line codes. False ⇒ 422 unknown-code, fail-closed.</summary>
    public bool CodesValid { get; set; } = true;

    public InMemoryOutbox Outbox { get; private set; } = default!;

    /// <summary>A fresh tenant per factory, so one run's leftovers cannot decide the next run's result.</summary>
    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Orders"] = Db,
                ["Events:UseInMemoryOutbox"] = "true",
            }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(OrdersTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, OrdersTestAuth>(OrdersTestAuth.SchemeName, _ => { });

            services.RemoveAll<ICodeValidator>();
            services.AddSingleton<ICodeValidator>(new FakeCodeValidator(this));
            services.RemoveAll<ITreatingRelationshipClient>();
            services.AddSingleton<ITreatingRelationshipClient>(new FakeTreatingRelationship(this));
            services.RemoveAll<IExaminationTypeResolver>();
            services.AddSingleton<IExaminationTypeResolver>(new FakeExaminationTypes());
            services.RemoveAll<IReportDocumentClient>();
            services.AddSingleton<IReportDocumentClient>(new FakeReportDocuments());
            services.RemoveAll<IBranchDirectory>();
            services.AddSingleton<IBranchDirectory>(new UnrestrictedBranches());

            // The expiry sweeper is a timer that wakes up mid-test and writes to the same tables the
            // assertions read. It has its own test; here it is only a source of flake.
            services.RemoveAll<IHostedService>();
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    /// <summary>The ordering doctor: creates and reads, and may not consume (that is the lab's action).</summary>
    public HttpClient DoctorClient() => As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read");

    /// <summary>A lab at a specific provider — the consume caller, isolated to its own provider's orders.</summary>
    public HttpClient LabClient(Guid providerId)
    {
        var c = As(OrdersTestAuth.LabSub, "lab_tech", "orders:consume orders:read");
        c.DefaultRequestHeaders.Add("X-Test-Provider", providerId.ToString());
        return c;
    }

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
            "DELETE FROM orders.order_fulfillment WHERE order_line_id IN " +
            "  (SELECT order_line_id FROM orders.order_line WHERE order_id IN " +
            "     (SELECT order_id FROM orders.investigation_order WHERE tenant_id = {0})); " +
            "DELETE FROM orders.order_line WHERE order_id IN " +
            "  (SELECT order_id FROM orders.investigation_order WHERE tenant_id = {0}); " +
            "DELETE FROM orders.investigation_order WHERE tenant_id = {0};", Tenant);
    }

    public static OrdersDbContext Ctx() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

internal sealed class FakeCodeValidator(OrdersApiFactory f) : ICodeValidator
{
    public Task<bool> IsValidAsync(CodeSystem system, string code, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(f.CodesValid);
}

internal sealed class FakeTreatingRelationship(OrdersApiFactory f) : ITreatingRelationshipClient
{
    public Task<bool> TreatsAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(f.Treats);
}

internal sealed class FakeExaminationTypes : IExaminationTypeResolver
{
    public Task<ExaminationClassification?> ResolveAsync(Guid examinationTypeId, string? bearer, CancellationToken ct = default)
        => Task.FromResult<ExaminationClassification?>(null);
}

internal sealed class FakeReportDocuments : IReportDocumentClient
{
    public Task<Guid?> StoreReportAsync(Guid beneficiaryId, string fileName, string contentType, byte[] content,
        string? bearerToken, CancellationToken ct = default) => Task.FromResult<Guid?>(Guid.NewGuid());
}

/// <summary>No permitted set — branch scoping has its own suite (BranchScope*Tests); here it must not decide
/// the outcome, and an empty set is what an unrestricted, non-BranchScoped role resolves to.</summary>
internal sealed class UnrestrictedBranches : IBranchDirectory
{
    public Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
        => Task.FromResult(PermittedBranches.None);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape, with provider_id for the ABAC
/// provider-ownership rule the consume gate keys on.</summary>
public sealed class OrdersTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string DoctorSub = "11111111-1111-1111-1111-111111111111";
    public const string LabSub = "22222222-2222-2222-2222-222222222222";

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
            claims.Add(new Claim("features", ProgramFeatures.Orders));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
