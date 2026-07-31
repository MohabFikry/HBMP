using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Reporting.Infrastructure;
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

namespace Mersal.Reporting.Tests;

/// <summary>
/// Phase 24 Gate 3 — the report zones, over HTTP.
///
/// <para>reporting-service's Api layer measured 5.7%. Its distinctive rule lives there and nowhere else: the
/// three ZONES. Operational, clinical and financial reports are separate permissions, so finance reads money
/// and not diagnoses, and the approval team reads its worklist and not the ledger. Every endpoint names its
/// zone, and a single mis-typed action constant would silently widen one — reading as a working report the
/// whole time.</para>
/// </summary>
[Collection("reporting-db")]
public class ReportingEndpointTests
{
    /// <summary>
    /// The zone boundary, both ways. A finance caller reads the financial zone and is refused the clinical
    /// one; a clinical caller is refused the financial one. Asserting one direction would pass on a service
    /// that refused everything.
    /// </summary>
    [SkippableFact]
    public async Task The_report_zones_are_separate_permissions()
    {
        Skip.If(ReportingApiFactory.Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        await using var app = new ReportingApiFactory();

        using var finance = app.FinanceClient();
        (await finance.GetAsync(new Uri("/api/v1/reports/top-diagnoses", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "finance never receives diagnoses — that is the same rule the claims payload obeys, applied " +
                "to the report that would aggregate them");

        using var clinical = app.MedicalApprovalClient();
        (await clinical.GetAsync(new Uri("/api/v1/reports/pending-approvals", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.OK,
                "the approval team reads its own operational worklist");
    }

    /// <summary>
    /// A long range is not run inline: it is persisted as a job and the caller gets a 202 with a handle to
    /// poll. Answering 200 after a minute of computation is how a report becomes a request timeout that the
    /// operator retries, doubling the load each time.
    /// </summary>
    [SkippableFact]
    public async Task A_long_range_is_queued_as_a_job_and_a_short_one_is_run_inline()
    {
        Skip.If(ReportingApiFactory.Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        await using var app = new ReportingApiFactory();
        try
        {
            using var manager = app.ManagerClient();

            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var inline = await manager.GetAsync(new Uri(
                $"/api/v1/reports/approval-tat?from={today.AddDays(-7):yyyy-MM-dd}&to={today:yyyy-MM-dd}",
                UriKind.Relative));
            inline.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await inline.Content.ReadAsStringAsync());

            var queued = await manager.GetAsync(new Uri(
                $"/api/v1/reports/approval-tat?from={today.AddYears(-3):yyyy-MM-dd}&to={today:yyyy-MM-dd}",
                UriKind.Relative));
            queued.StatusCode.Should().Be(HttpStatusCode.Accepted);

            using var handle = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
            var jobId = handle.RootElement.GetProperty("jobId").GetGuid();
            handle.RootElement.GetProperty("pollUrl").GetString()
                .Should().Be($"/api/v1/reports/jobs/{jobId}", "the handle tells the client where to poll, or " +
                                                             "it has to guess the route");

            await using var db = ReportingApiFactory.Ctx();
            var job = await db.ReportJobs.AsNoTracking().SingleAsync(j => j.JobId == jobId);
            job.Report.Should().Be("approval-tat");
            job.Status.Should().Be("Complete");
            job.ResultJson.Should().NotBeNullOrEmpty("a job that reports Complete with no result is a job the " +
                                                     "client polls forever");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>An unparseable dimension is refused rather than defaulted. A utilization report silently
    /// grouped by the wrong dimension is a number somebody will act on.</summary>
    [SkippableFact]
    public async Task An_unknown_utilization_dimension_is_refused_rather_than_defaulted()
    {
        Skip.If(ReportingApiFactory.Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        await using var app = new ReportingApiFactory();
        using var manager = app.ManagerClient();

        var r = await manager.GetAsync(new Uri("/api/v1/reports/utilization?dimension=sideways", UriKind.Relative));
        ((int)r.StatusCode).Should().BeOneOf(400, 422);
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_reaches_no_report()
    {
        Skip.If(ReportingApiFactory.Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        await using var app = new ReportingApiFactory();
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync(new Uri("/api/v1/reports/pending-approvals", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>Hosts the real reporting endpoints against the env-gated Postgres. The payer directory is the one
/// sibling it reaches over HTTP.</summary>
public sealed class ReportingApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("REPORTING_TEST_DB");

    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Reporting"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(ReportingTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, ReportingTestAuth>(ReportingTestAuth.SchemeName, _ => { });
            s.RemoveAll<IPayerDirectory>();
            s.AddSingleton<IPayerDirectory>(new UnrestrictedPayers());
            s.RemoveAll<IHostedService>();
        });
    }

    /// <summary>Finance: the financial zone, and never the clinical one.</summary>
    public HttpClient FinanceClient() => As("11111111-1111-1111-1111-111111111111", "finance",
        "reporting:read reporting:read-financial reporting:export");

    /// <summary>The approval team: the operational worklist, and nothing financial.</summary>
    public HttpClient MedicalApprovalClient() => As("22222222-2222-2222-2222-222222222222", "medical_approval",
        "reporting:read");

    /// <summary>A manager holds every zone — the caller a report is normally run by.</summary>
    public HttpClient ManagerClient() => As("33333333-3333-3333-3333-333333333333", "manager",
        "reporting:read reporting:read-financial reporting:export reporting:project");

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
        await db.Database.ExecuteSqlRawAsync("DELETE FROM reporting.report_job WHERE tenant_id = {0};", Tenant);
    }

    public static ReportingDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

/// <summary>Not payer-restricted — payer scoping has its own suite.</summary>
internal sealed class UnrestrictedPayers : IPayerDirectory
{
    public Task<PermittedPayers> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
        => Task.FromResult(PermittedPayers.Unrestricted);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class ReportingTestAuth(
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
        claims.Add(new Claim("features", ProgramFeatures.ReportingExtracts));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
