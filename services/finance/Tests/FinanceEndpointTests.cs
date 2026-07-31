using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Finance.Api;
using Mersal.Finance.Domain;
using Mersal.Finance.Infrastructure;
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

namespace Mersal.Finance.Tests;

/// <summary>
/// Phase 24 Gate 3 — the settlement lifecycle, over HTTP.
///
/// <para>finance-service's Api layer measured 0.0% over 264 lines, and the rule that lives there is a
/// segregation of duties on money: the approver of a settlement must be a different person than the
/// submitter. It is enforced in the endpoint and nowhere else, so one officer submitting and approving their
/// own payment run would have failed no test.</para>
/// </summary>
[Collection("finance-db")]
public class FinanceEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Draft → Submitted → Approved, with the SoD rule in the middle. The submitter is refused their own
    /// approval and a second officer is admitted; asserting only the refusal would pass on a service that
    /// approved nothing.
    /// </summary>
    [SkippableFact]
    public async Task A_settlement_cannot_be_approved_by_the_person_who_submitted_it()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var settlementId = await GenerateAsync(app, officer);

            (await officer.PostAsync(Settlement(settlementId, "submit"), null))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // The SAME subject, now also holding the manager role and the approve scope.
            using var sameOfficer = app.OfficerClient(approver: true);
            var selfApproval = await sameOfficer.PostAsync(Settlement(settlementId, "approve"), null);
            selfApproval.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await selfApproval.Content.ReadAsStringAsync()).Should().Contain("segregation-of-duties");

            using var manager = app.ManagerClient();
            var approved = await manager.PostAsync(Settlement(settlementId, "approve"), null);
            approved.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await approved.Content.ReadAsStringAsync());

            await using var db = FinanceApiFactory.Ctx();
            var row = await db.Settlements.AsNoTracking().SingleAsync(s => s.SettlementId == settlementId);
            row.Status.Should().Be(SettlementStatus.Approved);
            row.ApprovedBy.Should().NotBe(row.SubmittedBy, "the two signatures on a payment run are the point");

            app.Outbox.AllMessages.Select(m => m.EventType).Should().Contain("SettlementApproved",
                "an approved settlement whose event was lost is a payment authorised here and announced to " +
                "nothing downstream that acts on it");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The lifecycle is a state machine, not a set of independent buttons: a Draft cannot be
    /// approved, and a settlement cannot be submitted twice.</summary>
    [SkippableFact]
    public async Task The_settlement_states_are_ordered()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var settlementId = await GenerateAsync(app, officer);

            using var manager = app.ManagerClient();
            (await manager.PostAsync(Settlement(settlementId, "approve"), null))
                .StatusCode.Should().Be(HttpStatusCode.Conflict, "a Draft has not been submitted for approval");

            (await officer.PostAsync(Settlement(settlementId, "submit"), null))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await officer.PostAsync(Settlement(settlementId, "submit"), null))
                .StatusCode.Should().Be(HttpStatusCode.Conflict, "only a Draft can be submitted");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Generating a settlement mints a financial artifact, so a replayed Idempotency-Key returns the
    /// one produced the first time rather than a second payment run for the same period.</summary>
    [SkippableFact]
    public async Task Replaying_a_generate_key_returns_the_first_settlement()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var key = Guid.NewGuid().ToString();
            var body = Generate();

            var first = await PostAsync(officer, "/api/v1/finance/settlements", key, body);
            first.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await first.Content.ReadAsStringAsync());
            var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("settlementId").GetGuid();

            var replay = await PostAsync(officer, "/api/v1/finance/settlements", key, body);
            replay.StatusCode.Should().Be(HttpStatusCode.Created);
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("settlementId").GetGuid()
                .Should().Be(id);

            await using var db = FinanceApiFactory.Ctx();
            (await db.Settlements.CountAsync(s => s.TenantId == app.Tenant)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_backwards_period_is_refused_and_an_anonymous_caller_reaches_nothing()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var backwards = await PostAsync(officer, "/api/v1/finance/settlements", Guid.NewGuid().ToString(),
                Generate() with { PeriodStart = today, PeriodEnd = today.AddDays(-1) });
            backwards.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await backwards.Content.ReadAsStringAsync()).Should().Contain("bad-period");

            using var anonymous = app.CreateClient();
            (await anonymous.GetAsync(new Uri("/api/v1/finance/settlements", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static Uri Settlement(Guid id, string action) =>
        new($"/api/v1/finance/settlements/{id}/{action}", UriKind.Relative);

    private static GenerateSettlementRequest Generate()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        return new GenerateSettlementRequest(Guid.NewGuid(), today.AddDays(-30), today);
    }

    private static async Task<Guid> GenerateAsync(FinanceApiFactory app, HttpClient officer)
    {
        var r = await PostAsync(officer, "/api/v1/finance/settlements", Guid.NewGuid().ToString(), Generate());
        r.StatusCode.Should().Be(HttpStatusCode.Created,
            "the seed must succeed or every assertion below is vacuous: {0}", await r.Content.ReadAsStringAsync());
        _ = app;
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("settlementId").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string? idempotencyKey, object body)
    {
        // Awaited inside the using: returning the task would dispose the content mid-send.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }
}

/// <summary>Hosts the real finance endpoints. provider-service supplies the contract price book; a settlement
/// whose totals depended on a sibling's fixtures would be testing provider-service.</summary>
public sealed class FinanceApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("FINANCE_TEST_DB");

    public const string OfficerSub = "11111111-1111-1111-1111-111111111111";
    public const string ManagerSub = "22222222-2222-2222-2222-222222222222";

    public InMemoryOutbox Outbox { get; private set; } = default!;
    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Finance"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(FinanceTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, FinanceTestAuth>(FinanceTestAuth.SchemeName, _ => { });
            s.RemoveAll<IContractPriceProvider>();
            s.AddSingleton<IContractPriceProvider>(new NoPriceBook());
            s.RemoveAll<IHostedService>();
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    /// <summary>
    /// A finance officer: generates and submits.
    ///
    /// <para><paramref name="approver"/> gives the SAME PERSON the manager role and the approve scope as
    /// well — a multi-role principal, which is the only way the SoD rule can bite. The submit and approve
    /// RULES already grant disjoint role sets (finance submits; finance_approver/manager/medical_director
    /// approve), so a single-role officer is refused 403 by authorization long before the handler compares
    /// submitter to approver. The handler's check is what stops the person who holds both.</para>
    /// </summary>
    public HttpClient OfficerClient(bool approver = false) => approver
        ? As(OfficerSub, "finance manager", "finance:read finance:write finance:approve")
        : As(OfficerSub, "finance", "finance:read finance:write");

    /// <summary>A second, distinct principal holding the approval — the counter-signature.</summary>
    public HttpClient ManagerClient() => As(ManagerSub, "manager", "finance:read finance:write finance:approve");

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

    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM finance.settlement_line WHERE settlement_id IN " +
            "  (SELECT settlement_id FROM finance.settlement WHERE tenant_id = {0}); " +
            "DELETE FROM finance.settlement WHERE tenant_id = {0};", Tenant);
    }

    public static FinanceDbContext Ctx() =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

/// <summary>No price book — provider-service is not running, and the totals are not what this suite asserts.</summary>
internal sealed class NoPriceBook : IContractPriceProvider
{
    public Task<ContractPriceBook?> GetPriceBookAsync(Guid providerId, DateOnly asOf, string? bearerToken,
        CancellationToken ct = default) => Task.FromResult<ContractPriceBook?>(null);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class FinanceTestAuth(
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
        claims.Add(new Claim("features", ProgramFeatures.Finance));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
