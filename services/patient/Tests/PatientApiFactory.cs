using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Events;
using Mersal.Patient.Infrastructure;
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

namespace Mersal.Patient.Tests;

/// <summary>
/// Phase 24 Gate 3 — the beneficiary directory, hosted.
///
/// <para><b>Why this exists.</b> patient-service's Api layer measured 2.1% over 996 lines, and 18.B3 changed
/// its most consequential rule there and nowhere else: reads and writes stopped being the same permission.
/// Before that, everything under /beneficiaries required patient:write, so the desk could not look up the
/// person standing in front of it — and whoever COULD look someone up was, by the same token, allowed to
/// rewrite their identity record. That split is enforced entirely in the endpoint layer and had no test.</para>
///
/// <para>The service reaches no sibling over HTTP, so nothing here is faked but the token.</para>
/// </summary>
public sealed class PatientApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("PATIENT_TEST_DB");

    public InMemoryOutbox Outbox { get; private set; } = default!;
    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Patient"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(PatientTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, PatientTestAuth>(PatientTestAuth.SchemeName, _ => { });
            s.RemoveAll<IHostedService>();
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    /// <summary>Beneficiary management: registers and edits the identity record.</summary>
    public HttpClient RegistrarClient() =>
        As(PatientTestAuth.RegistrarSub, "beneficiary_mgmt", "patient:write patient:read");

    /// <summary>Reception: looks up the person at the desk, and may not rewrite them. This is the 18.B3
    /// separation, and it is the whole reason this harness exists.</summary>
    public HttpClient ReceptionClient() =>
        As(PatientTestAuth.ReceptionSub, "reception", "patient:read");

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
            "SET session_replication_role = replica; " +
            "DELETE FROM patient.registration_thread WHERE registration_id IN " +
            "  (SELECT registration_id FROM patient.registration WHERE tenant_id = {0}); " +
            "DELETE FROM patient.registration_note WHERE registration_id IN " +
            "  (SELECT registration_id FROM patient.registration WHERE tenant_id = {0}); " +
            "DELETE FROM patient.enrolment_intent WHERE registration_id IN " +
            "  (SELECT registration_id FROM patient.registration WHERE tenant_id = {0}); " +
            "DELETE FROM patient.registration WHERE tenant_id = {0}; " +
            "DELETE FROM patient.beneficiary_history WHERE beneficiary_id IN " +
            "  (SELECT beneficiary_id FROM patient.beneficiary WHERE tenant_id = {0}); " +
            "DELETE FROM patient.beneficiary_identifier WHERE beneficiary_id IN " +
            "  (SELECT beneficiary_id FROM patient.beneficiary WHERE tenant_id = {0}); " +
            "DELETE FROM patient.contact WHERE beneficiary_id IN " +
            "  (SELECT beneficiary_id FROM patient.beneficiary WHERE tenant_id = {0}); " +
            "DELETE FROM patient.beneficiary WHERE tenant_id = {0}; " +
            // 0008 — the registration idempotency ledger. Its rows outlive the beneficiary they point at
            // (no foreign key, deliberately), so a leftover would let one run's key decide the next run's
            // result — the exact failure a per-run tenant exists to prevent.
            "DELETE FROM patient.processed_request WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", Tenant);
    }

    public static PatientDbContext Ctx() =>
        new(new DbContextOptionsBuilder<PatientDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class PatientTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RegistrarSub = "11111111-1111-1111-1111-111111111111";
    public const string ReceptionSub = "22222222-2222-2222-2222-222222222222";

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
