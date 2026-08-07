using Mersal.Audit.Client;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Orders.Api;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Mersal.Validity;
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

    /// <summary>29.4 — a branch the caller is assigned to, or null for the default "no permitted set".
    ///
    /// <para>Opt-in for the same reason <see cref="Treats"/> is: `doctor` is a BRANCH-SCOPED role, so with no
    /// permitted branch <c>ApplyBranchScope</c> resolves to the no-branch sentinel and every branch-scoped
    /// query returns nothing. That is correct behaviour and the right default here — branch scoping has its
    /// own suite and must not silently decide unrelated outcomes — but a suite that exercises a branch-scoped
    /// READ needs a branch to read at, or it proves only that an empty set is empty.</para></summary>
    public Guid? PermittedBranch { get; set; }

    /// <summary>Every audit event this factory's app emitted, in order.
    ///
    /// <para>Captured at the <see cref="IAuditOutbox"/> seam rather than read back from
    /// <c>orders.outbox_message</c>, because the relay drains that table asynchronously — a test that queried
    /// it would pass or fail on timing, which for an audit assertion is the worst possible property. This
    /// records what was EMITTED; that it is then durable is <c>EfOutboxDurabilityTests</c>' job.</para></summary>
    public List<AuditEvent> AuditEvents { get; } = [];

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
            // The platform default, without reaching admin-service. The expiry is still STAMPED on every
            // order these tests create, so a regression that stopped setting one is still caught.
            services.RemoveAll<IValidityPolicySource>();
            services.AddSingleton<IValidityPolicySource>(new DefaultValidityPolicySource());
            services.RemoveAll<IExaminationTypeResolver>();
            services.AddSingleton<IExaminationTypeResolver>(new FakeExaminationTypes());
            // 29.2 — the OP-Procedure type resolver. The default fake knows the seeded Physiotherapy type, so
            // the procedure path is exercisable without a masterdata round-trip; tests that need a different
            // answer replace it.
            services.RemoveAll<IProcedureTypeResolver>();
            services.AddSingleton<IProcedureTypeResolver>(new FakeProcedureTypes());
            services.RemoveAll<IReportDocumentClient>();
            services.AddSingleton<IReportDocumentClient>(new FakeReportDocuments());
            services.RemoveAll<IBranchDirectory>();
            services.AddSingleton<IBranchDirectory>(new UnrestrictedBranches(this));
            services.RemoveAll<IAuditOutbox>();
            services.AddSingleton<IAuditOutbox>(new CapturingAuditOutbox(this));

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
            // 30.1 — the amendment ledger references the lines, so it goes first.
            "DELETE FROM orders.line_amendment WHERE order_id IN " +
            "  (SELECT order_id FROM orders.investigation_order WHERE tenant_id = {0}); " +
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

/// <summary>29.2 — mirrors the seeded masterdata rows closely enough to exercise the write-path check:
/// Physiotherapy is session-based and Medicine-only, MinorSurgery is neither. Sections follow the real CPT
/// ranges so a test code like 97110 (Medicine) or 29881 (Surgery) classifies the way it would in production.</summary>
internal sealed class FakeProcedureTypes : IProcedureTypeResolver
{
    public Task<ProcedureTypeLookup> ResolveAsync(
        string? typeCode, string? cptCode, string? bearer, CancellationToken ct = default)
    {
        var section = cptCode is null || cptCode.Length != 5 || !int.TryParse(cptCode, out var n) ? null
            : n is >= 10000 and <= 69999 ? "Surgery"
            : n is >= 70000 and <= 79999 ? "Imaging"
            : n is >= 80000 and <= 89999 ? "Laboratory"
            : n is >= 99202 and <= 99499 ? "EvaluationAndManagement"
            : n >= 90000 ? "Medicine"
            : "Anesthesia";

        ProcedureTypeFacts? facts = typeCode switch
        {
            "Physiotherapy" => new("Physiotherapy", IsSessionBased: true, MaxSessions: 30, ["Medicine"], IsActive: true),
            "MinorSurgery" => new("MinorSurgery", IsSessionBased: false, MaxSessions: null, ["Surgery"], IsActive: true),
            "Retired" => new("Retired", IsSessionBased: false, MaxSessions: null, ["Medicine"], IsActive: false),
            _ => null,
        };
        return Task.FromResult(new ProcedureTypeLookup(section, facts));
    }
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
/// <summary>Records every audit emit so a test can assert on it without racing the outbox relay.</summary>
internal sealed class CapturingAuditOutbox(OrdersApiFactory f) : IAuditOutbox
{
    public ValueTask EnqueueAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        lock (f.AuditEvents) f.AuditEvents.Add(auditEvent);
        return ValueTask.CompletedTask;
    }
}

internal sealed class UnrestrictedBranches(OrdersApiFactory f) : IBranchDirectory
{
    public Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
        => Task.FromResult(f.PermittedBranch is { } b
            ? new PermittedBranches(Home: b, Permitted: new HashSet<Guid> { b })
            : PermittedBranches.None);
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
