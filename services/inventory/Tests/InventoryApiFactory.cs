using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Inventory.Infrastructure;
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

namespace Mersal.Inventory.Tests;

/// <summary>
/// The inventory endpoints, hosted.
///
/// <para><b>Why this exists.</b> `services/inventory:Api` measured <b>0.0% over 459 lines</b> — the layer had
/// no test at all. Everything BENEATH it was covered (`StockRules`, `MovementService`, `BranchReachGuard`)
/// and every one of those runs below HTTP, so the rules that live only in the handlers had nothing proving
/// them: that `Idempotency-Key` is REQUIRED rather than optional, that a coordinator cannot post a movement
/// at a clinic they do not run, that a transfer is refused as a single movement, that the failure outcomes
/// map to the right status codes, and that no route accepts a beneficiary identifier at runtime rather than
/// merely in a source scan.</para>
///
/// <para>The floor recorded for this layer was <b>0</b>, which enforces nothing — a placeholder so the module
/// was visible in the list rather than absent from it. This is what replaces it.</para>
/// </summary>
public sealed class InventoryApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("INVENTORY_TEST_DB");

    public string Tenant { get; } = "t-inv-api-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>The branches the caller is granted. The reach rules read this exactly as production does —
    /// only the SOURCE is faked, never the decision.</summary>
    public HashSet<Guid> PermittedBranches { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        // UseSetting, not ConfigureAppConfiguration: Program reads these off builder.Configuration while it
        // is still executing, BEFORE the host-level configuration callback would have been applied. The same
        // lesson profile-service's factory records — with the callback the host fails at
        // AddHbmpAuthentication with "Auth:Authority must be configured", which reads like a missing secret
        // rather than a wrong extension point.
        builder.UseSetting("ConnectionStrings:Inventory", Db ?? "");
        builder.UseSetting("Auth:Authority", "https://identity.test");
        builder.UseSetting("Auth:Audience", "hbmp");
        builder.UseSetting("Events:UseInMemoryOutbox", "true");
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(InventoryTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, InventoryTestAuth>(InventoryTestAuth.SchemeName, _ => { });

            // The ONLY substitution: where the permitted set comes from. `BranchScopeResolver`,
            // `BranchReachGuard` and `BranchQueryScope` are the real ones — swapping the decision instead of
            // the lookup would leave the reach rules untested by the suite written to test them.
            s.RemoveAll<IBranchDirectory>();
            s.AddSingleton<IBranchDirectory>(new FakeBranchDirectory(this));

            // The outbox relay runs against a broker that is not here.
            s.RemoveAll<IHostedService>();
        });
    }

    /// <summary>A branch coordinator: one clinic, BranchScoped. Pass the branch to send X-Active-Branch.</summary>
    public HttpClient CoordinatorClient(Guid? activeBranch = null) =>
        As("11111111-1111-1111-1111-111111111111", "branch_coordinator",
            "branch:inventory:read branch:inventory:write", activeBranch);

    /// <summary>A clinics manager: BranchSetScoped. No active branch ⇒ every branch in reach, in one call.</summary>
    public HttpClient ManagerClient(Guid? filter = null) =>
        As("22222222-2222-2222-2222-222222222222", "clinics_manager",
            "branch:inventory:read branch:inventory:write", filter);

    /// <summary>Read-only: holds the read scope and not the write one.</summary>
    public HttpClient ReadOnlyClient(Guid? activeBranch = null) =>
        As("33333333-3333-3333-3333-333333333333", "branch_coordinator", "branch:inventory:read", activeBranch);

    public HttpClient As(string sub, string role, string scopes, Guid? branchId = null)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        c.DefaultRequestHeaders.Add("X-Test-Role", role);
        c.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        if (branchId is { } b) c.DefaultRequestHeaders.Add("X-Active-Branch", b.ToString());
        return c;
    }

    public InventoryDbContext Ctx() =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        // stock_movement refuses DELETE by trigger — the cleanup has to disable it, which is the plainest
        // possible statement of what that trigger does.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE inventory.stock_movement DISABLE TRIGGER trg_stock_movement_no_mutate;
            DELETE FROM inventory.stock_movement WHERE tenant_id = {0};
            ALTER TABLE inventory.stock_movement ENABLE TRIGGER trg_stock_movement_no_mutate;
            DELETE FROM inventory.stock_batch  WHERE tenant_id = {0};
            DELETE FROM inventory.branch_item  WHERE tenant_id = {0};
            DELETE FROM inventory.item_history WHERE tenant_id = {0};
            DELETE FROM inventory.item         WHERE tenant_id = {0};
            """, Tenant);
    }
}

internal sealed class FakeBranchDirectory(InventoryApiFactory f) : IBranchDirectory
{
    public Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default) =>
        Task.FromResult(new PermittedBranches(
            f.PermittedBranches.FirstOrDefault() == Guid.Empty ? null : f.PermittedBranches.First(),
            f.PermittedBranches));
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class InventoryTestAuth(
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
