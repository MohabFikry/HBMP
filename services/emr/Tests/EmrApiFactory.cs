using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
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

namespace Mersal.Emr.Tests;

/// <summary>
/// Phase 24 Gate 3 — the emr endpoints, hosted.
///
/// <para><b>Why this exists.</b> emr-service's Api layer measured 0.8% over 1,846 lines. The domain suite is
/// strong on the appointment state machine and the visit gate, and it all runs below HTTP, so the endpoint
/// rules had no test: the visit gate that refuses to open an encounter for a member who is not Active, the
/// branch resolution that a branch-scoped caller cannot talk its way out of, and the practitioner-branch
/// check that stops slots being materialized for a doctor who does not work at that branch — which the code
/// itself calls the gate that matters more, because refusing there means no patient is ever booked into
/// them.</para>
/// </summary>
public sealed class EmrApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("EMR_TEST_DB");

    /// <summary>What policy-service says about the member. Anything but Active blocks the visit.</summary>
    public MemberStatus? MemberStatus { get; set; } = Mersal.Emr.Domain.MemberStatus.Active;

    /// <summary>Whether the named doctor serves the named branch. Null models "the directory could not be
    /// reached", which PractitionerBranchRules treats on its own terms.</summary>
    public bool? DoctorServesBranch { get; set; } = true;

    /// <summary>25.3 — the doctor's licence expiry the fake probe reports. Null ⇒ UNKNOWN licence validity,
    /// which is deliberately the pre-25.3 behaviour so every existing booking test is unaffected by the new
    /// gate; a licence suite sets it to move the boundary around the date under test.</summary>
    public DateOnly? DoctorLicenceExpiry { get; set; }

    /// <summary>
    /// Whether master data recognises the codes a write refers to. True (the default) is the shipped
    /// accept-everything validator; false models "masterdata does not hold this code", which is how the
    /// fail-closed refusals are exercised — an allergen with no name is refused rather than recorded.
    /// </summary>
    public bool ValidateCodes { get; set; } = true;

    public InMemoryOutbox Outbox { get; private set; } = default!;

    public string Tenant { get; } = "t-api-" + Guid.NewGuid().ToString("N")[..10];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Emr"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(EmrTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, EmrTestAuth>(EmrTestAuth.SchemeName, _ => { });

            s.RemoveAll<IMemberStatusProvider>();
            s.AddSingleton<IMemberStatusProvider>(new FakeMemberStatus(this));
            s.RemoveAll<IClinicalCodeValidator>();
            // The shipped accept-everything validator; code validation now has its own suite in masterdata.
            s.AddSingleton<IClinicalCodeValidator>(
                ValidateCodes ? new AllowAllClinicalCodeValidator() : new RecognisesNothingValidator());
            s.RemoveAll<IPractitionerBranchDirectory>();
            s.AddSingleton<IPractitionerBranchDirectory>(new FakePractitionerBranches(this));
            s.RemoveAll<IBranchDirectory>();
            s.AddSingleton<IBranchDirectory>(new NoBranchRestriction());

            // The branch-revoked consumer listens on a broker that is not running here, and the reminder
            // sweep writes to the tables the assertions read.
            s.RemoveAll<IHostedService>();
        });
    }

    public new HttpClient CreateClient()
    {
        var c = base.CreateClient();
        Outbox = (InMemoryOutbox)Services.GetRequiredService<InMemoryOutbox>();
        return c;
    }

    /// <summary>Reception: books, reschedules, cancels, checks in. No clinical write.</summary>
    public HttpClient ReceptionClient(string? displayName = null) => As(EmrTestAuth.ReceptionSub, "reception",
        "appointment:write appointment:reserve appointment:read", displayName: displayName);

    /// <summary>A treating doctor: opens encounters and writes the clinical record.</summary>
    public HttpClient DoctorClient() => As(EmrTestAuth.DoctorSub, "doctor",
        "emr:read emr:write encounter:write appointment:read appointment:write appointment:reserve");

    public HttpClient As(string sub, string role, string scopes, Guid? branchId = null, string? displayName = null)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        if (displayName is not null) c.DefaultRequestHeaders.Add("X-Test-Name", displayName);
        c.DefaultRequestHeaders.Add("X-Test-Role", role);
        c.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        if (branchId is { } b) c.DefaultRequestHeaders.Add("X-Active-Branch", b.ToString());
        return c;
    }

    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM emr.appointment_queue WHERE tenant_id = {0}; " +
            "DELETE FROM emr.queue_entry WHERE tenant_id = {0}; " +
            "DELETE FROM emr.waitlist_entry WHERE tenant_id = {0}; " +
            "DELETE FROM emr.appointment WHERE tenant_id = {0}; " +
            "DELETE FROM emr.appointment_slot WHERE tenant_id = {0}; " +
            "DELETE FROM emr.provider_availability WHERE tenant_id = {0}; " +
            "DELETE FROM emr.encounter WHERE tenant_id = {0};", Tenant);
    }

    public static EmrDbContext Ctx() =>
        new(new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

internal sealed class FakeMemberStatus(EmrApiFactory f) : IMemberStatusProvider
{
    public Task<MemberStatus?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(f.MemberStatus);
}


internal sealed class FakePractitionerBranches(EmrApiFactory f) : IPractitionerBranchDirectory
{
    public Task<PractitionerBookability> BookabilityAsync(
        Guid practitionerId, Guid branchId, DateOnly asOf, CancellationToken ct = default)
        => Task.FromResult(new PractitionerBookability(
            f.DoctorServesBranch,
            // 25.3 — the fake decides licence validity FROM the date under test, so a suite can move the
            // expiry rather than the answer. Null expiry ⇒ unknown, which is the pre-25.3 behaviour every
            // existing booking test relies on.
            f.DoctorLicenceExpiry is { } e ? e >= asOf : null,
            f.DoctorLicenceExpiry));
}

internal sealed class NoBranchRestriction : IBranchDirectory
{
    public Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
        => Task.FromResult(PermittedBranches.None);
}

/// <summary>Builds a principal from X-Test-* headers; the house shape.</summary>
public sealed class EmrTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string ReceptionSub = "11111111-1111-1111-1111-111111111111";
    public const string DoctorSub = "22222222-2222-2222-2222-222222222222";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("sub", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Role", out var role))
            foreach (var r in role.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("role", r));
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        // 0022 — the `name` claim, so a test can exercise the display-name snapshot on a written note. Without
        // it every principal here is nameless, which is exactly the shape that put a uuid on screen.
        if (Request.Headers.TryGetValue("X-Test-Name", out var displayName)) claims.Add(new Claim("name", displayName.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) claims.Add(new Claim("tenant_id", tenant.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));
        claims.Add(new Claim("features", ProgramFeatures.Emr));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>
/// A validator that recognises no code at all — the "masterdata does not hold this" case.
///
/// <para>The counterpart to <c>AllowAllClinicalCodeValidator</c>. Writes are supposed to FAIL CLOSED against
/// an unknown code, and the accept-everything default cannot demonstrate that; without this, every refusal
/// path in the clinical write endpoints was unreachable from a test.</para>
/// </summary>
internal sealed class RecognisesNothingValidator : IClinicalCodeValidator
{
    public Task<bool> IcdExistsAsync(string icdCode, string? bearerToken, CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> AllergenNameAsync(Guid allergenId, string? bearerToken, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> DrugExistsAsync(Guid drugId, string? bearerToken, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>LOINC is optional and accepted-and-recorded platform-wide; unchanged here so this fake
    /// differs from the default in exactly the way it claims to.</summary>
    public Task<bool> LoincValidAsync(string? loincCode, string? bearerToken, CancellationToken ct = default) => Task.FromResult(true);
}
