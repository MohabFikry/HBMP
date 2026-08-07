using System.Security.Claims;
using System.Text.Encodings.Web;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Infrastructure;
using Mersal.Validity;
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

namespace Mersal.Pharmacy.Tests;

/// <summary>Serializes the prescribing API tests — they write to the shared pharmacy schema.</summary>
[Xunit.CollectionDefinition("prescribing-api", DisableParallelization = true)]
public sealed class PrescribingApiTestGroup;

/// <summary>
/// Hosts the real prescribing endpoints against the env-gated Postgres (phase 26.4).
/// </summary>
/// <remarks>
/// The clinical data sources are stubbed — the point of these tests is the ENDPOINT's behaviour: what it
/// re-validates, what it refuses, and what it records. The engine itself is proved in
/// <c>Mersal.ClinicalValidation.Tests</c>, and the transport-failure behaviour in
/// <c>DependencyDownYieldsUnavailableTests</c>.
/// </remarks>
public sealed class PrescribingApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB");

    public Guid Beneficiary { get; } = Guid.NewGuid();
    public Guid Encounter { get; } = Guid.NewGuid();
    public Guid DrugA { get; } = Guid.NewGuid();
    public Guid DrugB { get; } = Guid.NewGuid();

    /// <summary>The stub the tests steer to produce a given clinical picture.</summary>
    public StubPorts Ports { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Pharmacy"] = Db ?? "Host=localhost;Port=1;Database=x;Username=x;Password=x",
            ["Events:UseInMemoryOutbox"] = "true",
        }));

        // A server-side exception surfaces to the client as a bare 500, which tells a failing test nothing.
        // Set HBMP_TEST_LOG to a path to capture what actually threw.
        if (Environment.GetEnvironmentVariable("HBMP_TEST_LOG") is { Length: > 0 } logPath)
        {
            builder.ConfigureLogging(l => l.AddProvider(new FileLoggerProvider(logPath)));
        }

        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IHostedService>();
            s.AddAuthentication(PrescribingTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, PrescribingTestAuth>(PrescribingTestAuth.SchemeName, _ => { });

            // Every drug exists: this suite is about validation outcomes, not master-data lookup.
            s.RemoveAll<IDrugValidator>();
            s.AddSingleton<IDrugValidator>(new AllowAllDrugValidator());

            s.RemoveAll<IClinicalValidationPorts>();
            s.AddSingleton<IClinicalValidationPorts>(Ports);

            // The prescriber treats this patient. Without a stub this fails CLOSED against a non-existent
            // emr-service and every request is a 403 — correct behaviour, but it would hide what these
            // tests are actually about. The treating-relationship gate itself is proved in PharmacyAuthzTests.
            s.RemoveAll<ITreatingRelationshipClient>();
            s.AddSingleton<ITreatingRelationshipClient>(new AlwaysTreatingClient());

            // The platform default period, without reaching admin-service. Note what is NOT stubbed away:
            // the expiry is still STAMPED on every prescription these tests write, so a change that stopped
            // setting one would still be caught here.
            s.RemoveAll<IValidityPolicySource>();
            s.AddSingleton<IValidityPolicySource>(new DefaultValidityPolicySource());
        });
    }

    /// <summary>A treating doctor. <paramref name="scopes"/> overrides the issuer's doctor set, for the tests
    /// that assert an endpoint is still gated when a scope is absent.</summary>
    public HttpClient Prescriber(string? scopes = null)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", "22222222-2222-2222-2222-222222222222");
        c.DefaultRequestHeaders.Add("X-Test-Role", "doctor");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "11111111-1111-1111-1111-111111111111");
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        // EXACTLY the scopes the issuer grants a doctor — see IdentityContract's role map. This fixture used
        // to add `pharmacy:read` as well, which no real prescriber holds; that is the DISPENSER's scope. The
        // extra scope made GET /prescriptions/mine pass here and 403 in production, so a doctor's own
        // prescriptions vanished from the encounter the moment they were saved and every test stayed green.
        //
        // A fixture more generous than the issuer does not test the system; it tests a system nobody runs.
        c.DefaultRequestHeaders.Add("X-Test-Scope", scopes ?? "rx:write rx:read");
        c.DefaultRequestHeaders.Add("X-Test-Features", "pharmacy");
        return c;
    }

    public static PharmacyDbContext Ctx() =>
        new(new DbContextOptionsBuilder<PharmacyDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            // 30.1 — the amendment ledger references the lines, so it goes first.
            "DELETE FROM pharmacy.line_amendment WHERE prescription_id IN " +
            "  (SELECT prescription_id FROM pharmacy.prescription WHERE beneficiary_id = {0}); " +
            "DELETE FROM pharmacy.prescription_line_override WHERE prescription_id IN " +
            "  (SELECT prescription_id FROM pharmacy.prescription WHERE beneficiary_id = {0}); " +
            "DELETE FROM pharmacy.prescription_validation WHERE beneficiary_id = {0}; " +
            "DELETE FROM pharmacy.prescription_alert WHERE prescription_id IN " +
            "  (SELECT prescription_id FROM pharmacy.prescription WHERE beneficiary_id = {0}); " +
            "DELETE FROM pharmacy.prescription_line WHERE prescription_id IN " +
            "  (SELECT prescription_id FROM pharmacy.prescription WHERE beneficiary_id = {0}); " +
            "DELETE FROM pharmacy.prescription WHERE beneficiary_id = {0};",
            Beneficiary);
    }
}

/// <summary>The prescriber treats the patient — the relationship gate is proved elsewhere.</summary>
public sealed class AlwaysTreatingClient : ITreatingRelationshipClient
{
    public Task<bool> TreatsAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>A steerable clinical-data source, so a test can state the picture it needs in one line.</summary>
public sealed class StubPorts : IClinicalValidationPorts
{
    private static readonly ProvenanceInfo Source = new("Test source", "R-test", DateTimeOffset.UnixEpoch);

    /// <summary>Interaction pairs the "list" holds. Empty means a populated list that found nothing.</summary>
    public List<InteractionFact> Interactions { get; } = [];

    /// <summary>Indications per drug, as ICD categories.</summary>
    public Dictionary<Guid, List<string>> Indications { get; } = [];

    public List<AllergyConflict> AllergyConflicts { get; } = [];
    public int RecordedAllergenCount { get; set; } = 1;

    /// <summary>
    /// Recorded drug allergens the catalogue holds no mapping for. Empty by default, so a fixture that says
    /// nothing about mapping describes a fully screened patient — the only state entitled to report Ok.
    /// </summary>
    public List<string> UnmappedAllergens { get; } = [];

    /// <summary>
    /// How many recorded allergens were actually compared. Null means "all of them", which is what a test
    /// not concerned with mapping coverage means.
    /// </summary>
    public int? ScreenedAllergenCount { get; set; }
    public List<BenefitOutcome> BenefitOutcomes { get; } = [];

    /// <summary>Drug id → why no manufacturer label was used for it.</summary>
    public Dictionary<Guid, string> Labels { get; } = [];

    /// <summary>Drug id → the molecules it contains and its ATC-4 class, for the duplication check.</summary>
    public Dictionary<Guid, DrugComposition> Compositions { get; } = [];

    /// <summary>Contraindication rules that fired for this prescription.</summary>
    public List<ContraindicationFact> Contraindications { get; } = [];

    /// <summary>How many rules the list holds. Zero makes the check report NotChecked.</summary>
    public int ContraindicationRuleCount { get; set; } = 8;

    /// <summary>The patient the engine sees. An adult weighed today unless a test says otherwise.</summary>
    public PatientContext Patient { get; set; } = new(AgeYears: 40, WeightKg: 70, WeightMeasuredAt: DateTimeOffset.UtcNow);

    /// <summary>Drug id → the catalogue name findings should refer to the medicine by.</summary>
    public Dictionary<Guid, string> DrugNames { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> DrugNamesAsync(
        IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(DrugNames);

    /// <summary>
    /// Diagnoses the SERVER would read from the encounter, per encounter id.
    /// </summary>
    /// <remarks>
    /// Keyed on encounter rather than set globally so a test can prove the thing that matters: the
    /// submission's own <c>diagnosisIcdCodes</c> is not consulted, and what reaches the engine is what this
    /// dictionary holds for the encounter the prescription names.
    /// </remarks>
    public Dictionary<Guid, List<string>> EncounterDiagnoses { get; } = [];

    /// <summary>Set to make the encounter-diagnosis fetch fail, the way an emr outage would.</summary>
    public string? DiagnosisFetchFailure { get; set; }

    public Task<ValidationSnapshot> FetchAsync(
        Guid beneficiaryId, IReadOnlyList<Guid> drugIds, Guid? encounterId,
        IReadOnlyList<string>? clientDiagnoses, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(new ValidationSnapshot(
            Fetched.From<IReadOnlyDictionary<Guid, DrugIndicationFact>>(
                Indications.ToDictionary(kv => kv.Key, kv => new DrugIndicationFact(kv.Key, kv.Value)), Source),
            Fetched.From(new InteractionTable(Interactions, KnownPairCount: 500), Source),
            Fetched.From(
                new AllergyScreen(
                    AllergyConflicts, RecordedAllergenCount, UnmappedAllergens,
                    ScreenedAllergenCount ?? RecordedAllergenCount),
                Source),
            Fetched.From<IReadOnlyDictionary<Guid, DosingRuleFact>>(new Dictionary<Guid, DosingRuleFact>(), Source),
            Fetched.From<IReadOnlyList<BenefitOutcome>>(BenefitOutcomes, Source),
            // No manufacturer labels in the API-level fixture: these tests are about the endpoints, and a
            // stub that reached openFDA would make them depend on the internet. An empty-but-available
            // evidence set makes the label check report "not checked", which is the honest answer for it.
            Fetched.From(
                new LabelEvidence(
                    new Dictionary<Guid, DrugLabelFact>(),
                    Labels.ToDictionary(kv => kv.Key, kv => kv.Value),
                    new Dictionary<Guid, string>()),
                Source),
            // The authoritative path passes an encounter id and NO client list; the advisory path the
            // reverse. Mirroring that here is what lets ForgedClientVerdictTests assert that a forged array
            // in the request body cannot reach the engine.
            encounterId is { } enc
                ? DiagnosisFetchFailure is { } why
                    ? Fetched.NotAvailable<DiagnosisContext>(why)
                    : Fetched.From(
                        new DiagnosisContext(
                            EncounterDiagnoses.TryGetValue(enc, out var dx) ? dx : [],
                            DiagnosisProvenance.EncounterFetched),
                        Source)
                : Fetched.From(
                    new DiagnosisContext(clientDiagnoses ?? [], DiagnosisProvenance.ClientSupplied), Source),
            Fetched.From<IReadOnlyDictionary<Guid, DrugComposition>>(Compositions, Source),
            Fetched.From(Patient, Source),
            Fetched.From(new ContraindicationTable(Contraindications, ContraindicationRuleCount), Source)));
}

/// <summary>Builds a principal from X-Test-* headers, matching the other services' convention.</summary>
public sealed class PrescribingTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("sub", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Role", out var role)) claims.Add(new Claim("role", role.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) claims.Add(new Claim("tenant_id", tenant.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));
        // 21.4 — the programme gate, asked after authorization. A tenant that is not onboarded onto the
        // pharmacy programme is refused, so a token carrying a tenant must also carry the feature.
        foreach (var f in Request.Headers["X-Test-Features"].ToString()
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            claims.Add(new Claim("features", f));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>
/// Writes server-side log entries to a file so a failing integration test can say WHY it got a 500.
/// </summary>
/// <remarks>
/// Enabled only when <c>HBMP_TEST_LOG</c> is set. UseExceptionHandler turns an unhandled exception into a
/// bare 500, and diagnosing that from the client side is guesswork.
/// </remarks>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private static readonly object Gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(path, categoryName);

    public void Dispose() => GC.SuppressFinalize(this);

    private sealed class FileLogger(string path, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);
            lock (Gate)
            {
                File.AppendAllText(path,
                    $"[{logLevel}] {category}: {formatter(state, exception)}\n{exception}\n\n");
            }
        }
    }
}
