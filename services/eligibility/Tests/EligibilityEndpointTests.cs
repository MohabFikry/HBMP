using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Eligibility.Api;
using Mersal.Eligibility.Infrastructure;
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

namespace Mersal.Eligibility.Tests;

/// <summary>
/// Phase 24 Gate 3 — the eligibility check, over HTTP.
///
/// <para>eligibility-service's Api layer measured 5.3%. The engine itself is the best-covered domain in the
/// platform (95%), and the endpoint around it was not exercised at all — including the rule that a check with
/// no benefit category is refused rather than answered about nothing, and the one that decides whether a cost
/// share may be quoted at all. A verdict served beside a co-pay derived from no plan version would be a
/// number the desk reads to a member.</para>
/// </summary>
[Collection("eligibility-db")]
public class EligibilityEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 32.6 — THIS TEST USED TO ASSERT A 400, and the assertion was right about the rule and wrong about
    /// what the rule cost.
    ///
    /// <para>Its reasoning was that "is this member covered" is not a question without naming what for, which
    /// is true. What happened in the product was not that callers named one: the reception desk stopped
    /// calling this endpoint at all and computed a verdict in the browser from a cached member status. So the
    /// tier, the plan version in force, the waiting period, the limits and the audit event were all absent
    /// from what a beneficiary was told at the desk — and the check that was supposed to be refused was
    /// simply never made.</para>
    ///
    /// <para>The category-less question is now ANSWERED, at membership scope, labelled as such. The old
    /// assertion is not deleted quietly: it is recorded here, because a future reader who reinstates the 400
    /// on the strength of its reasoning would reinstate the defect with it.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_check_without_a_benefit_category_answers_about_the_membership_and_says_so()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        using var reception = app.CheckerClient();
        var beneficiaryId = Guid.NewGuid();
        await app.SeedMemberAsync(beneficiaryId, "Active");

        try
        {
            var r = await reception.PostAsJsonAsync("/api/v1/eligibility/check",
                new EligibilityCheckRequest(beneficiaryId, "  ", null, null, null, null, null, null, null), Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("decisionScope").GetString().Should().Be("Membership",
                "the answer and what it is ABOUT travel together — 'Eligible' means two different things "
                + "at the two scopes and renders as the same word");
            doc.RootElement.GetProperty("decision").GetString().Should().Be("Eligible");

            // The bound on the answer is IN the answer, not left to the reader.
            doc.RootElement.GetProperty("reasons").EnumerateArray()
                .Select(x => x.GetString()).Should()
                .Contain(x => x!.Contains("no benefit category", StringComparison.Ordinal));

            // And no cost share is quoted — with the reason attached, so "no copay shown" cannot be read as
            // "no copay due".
            var share = doc.RootElement.GetProperty("costShare");
            share.GetProperty("determinate").GetBoolean().Should().BeFalse();
            share.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
        }
        finally { await app.CleanupMemberAsync(beneficiaryId); }
    }

    [SkippableFact]
    public async Task A_membership_check_on_a_suspended_member_is_ineligible()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        using var reception = app.CheckerClient();
        var beneficiaryId = Guid.NewGuid();
        await app.SeedMemberAsync(beneficiaryId, "Suspended");

        try
        {
            var r = await reception.PostAsJsonAsync("/api/v1/eligibility/check",
                new EligibilityCheckRequest(beneficiaryId, null, null, null, null, null, null, null, null), Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("decision").GetString().Should().Be("Ineligible");
            doc.RootElement.GetProperty("decisionScope").GetString().Should().Be("Membership");
        }
        finally { await app.CleanupMemberAsync(beneficiaryId); }
    }

    [SkippableFact]
    public async Task A_membership_check_writes_no_snapshot()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        using var reception = app.CheckerClient();
        var beneficiaryId = Guid.NewGuid();
        await app.SeedMemberAsync(beneficiaryId, "Active");

        try
        {
            (await reception.PostAsJsonAsync("/api/v1/eligibility/check",
                new EligibilityCheckRequest(beneficiaryId, null, null, null, null, null, null, null, null), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // A snapshot row is keyed by beneficiary AND category. Writing one under an invented category —
            // or under the empty string — would corrupt the next real check for it.
            await using var db = EligibilityApiFactory.Ctx();
            (await db.Snapshots.CountAsync(x => x.BeneficiaryId == beneficiaryId)).Should().Be(0);
        }
        finally { await app.CleanupMemberAsync(beneficiaryId); }
    }

    [SkippableFact]
    public async Task A_benefit_check_still_says_which_scope_it_answered_at()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        using var reception = app.CheckerClient();
        var beneficiaryId = Guid.NewGuid();
        await app.SeedMemberAsync(beneficiaryId, "Active");

        try
        {
            var r = await reception.PostAsJsonAsync("/api/v1/eligibility/check",
                new EligibilityCheckRequest(beneficiaryId, "LAB", null, null, null, null, null, null, null), Web);

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("decisionScope").GetString().Should().Be("Benefit");
        }
        finally { await app.CleanupMemberAsync(beneficiaryId); }
    }

    /// <summary>
    /// A member with no coverage is answered Ineligible — not 404, and not an error. The desk needs a verdict
    /// it can read out, and "we have no record" and "they are not covered" are the same answer at the counter.
    /// </summary>
    [SkippableFact]
    public async Task A_member_with_no_coverage_gets_a_verdict_not_an_error()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        using var reception = app.CheckerClient();

        var r = await reception.PostAsJsonAsync("/api/v1/eligibility/check",
            new EligibilityCheckRequest(Guid.NewGuid(), "LAB", null, null, null, null, null, null, null), Web);
        r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("decision").GetString().Should().NotBe("Eligible",
            "a beneficiary with no coverage row is not eligible, whatever else is true of them");
        doc.RootElement.GetProperty("reasons").EnumerateArray().Should().NotBeEmpty(
            "a refusal at the counter has to say why, or the member is turned away with nothing to act on");
        doc.RootElement.GetProperty("coverageId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// No plan version, no quote. Until enrolment links member → policy_plan → plan_version the caller
    /// supplies it, and without it the verdict is served with NO cost share rather than a number derived from
    /// nothing — which the desk would read to the member as if it were the price.
    /// </summary>
    [SkippableFact]
    public async Task A_check_with_no_plan_version_carries_a_verdict_and_no_cost_share()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        using var reception = app.CheckerClient();

        var r = await reception.PostAsJsonAsync("/api/v1/eligibility/check",
            new EligibilityCheckRequest(Guid.NewGuid(), "LAB", "80053", false,
                PlanVersionId: null, ProviderId: Guid.NewGuid(), LocationId: null,
                ServiceDate: null, EstimatedAmount: 500m), Web);
        r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("costShare", out var costShare))
            costShare.ValueKind.Should().Be(JsonValueKind.Null,
                "a co-pay quoted with no plan version behind it is a number invented at the counter");
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_and_a_wrong_scope_both_reach_nothing()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();

        using var anonymous = app.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/v1/eligibility/check",
            new EligibilityCheckRequest(Guid.NewGuid(), "LAB", null, null, null, null, null, null, null), Web))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var wrongScope = app.As("44444444-4444-4444-4444-444444444444", "reception", "orders:read");
        (await wrongScope.PostAsJsonAsync("/api/v1/eligibility/check",
            new EligibilityCheckRequest(Guid.NewGuid(), "LAB", null, null, null, null, null, null, null), Web))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

/// <summary>Hosts the real eligibility endpoints against the env-gated Postgres.</summary>
public sealed class EligibilityApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB");

    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Eligibility"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(EligibilityTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, EligibilityTestAuth>(EligibilityTestAuth.SchemeName, _ => { });
            // The event consumer listens on a broker that is not running here.
            s.RemoveAll<IHostedService>();
        });
    }

    /// <summary>Reception at the counter: the caller a check is normally made by.</summary>
    public HttpClient CheckerClient() => As("11111111-1111-1111-1111-111111111111", "reception",
        "eligibility:check eligibility:read");

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

    /// <summary>32.6 — a member projection at a named lifecycle status. The membership check reads this row
    /// and nothing else, so the status is the whole input.</summary>
    public async Task SeedMemberAsync(Guid beneficiaryId, string status)
    {
        await using var db = Ctx();
        db.Members.Add(new MemberProjection
        {
            TenantId = Tenant, BeneficiaryId = beneficiaryId, GivenName = "Walk", FamilyName = "In",
            Status = status, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task CleanupMemberAsync(Guid beneficiaryId)
    {
        await using var db = Ctx();
        await db.Snapshots.Where(s => s.BeneficiaryId == beneficiaryId).ExecuteDeleteAsync();
        await db.Coverages.Where(c => c.BeneficiaryId == beneficiaryId).ExecuteDeleteAsync();
        await db.Members.Where(m => m.BeneficiaryId == beneficiaryId).ExecuteDeleteAsync();
    }

    public static EligibilityDbContext Ctx() =>
        new(new DbContextOptionsBuilder<EligibilityDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class EligibilityTestAuth(
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
        _ = typeof(ProgramFeatures);

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Serializes the eligibility endpoint tests against the shared eligibility store.</summary>
[Xunit.CollectionDefinition("eligibility-db", DisableParallelization = true)]
public sealed class EligibilityDbTestGroup;
