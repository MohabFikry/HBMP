using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Mersal.Audit.Client;
using Mersal.Interop.Domain.Model;
using Mersal.Interop.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mersal.Interop.Tests;

/// <summary>Web-host test factory for the FHIR façade: swaps JwtBearer for a controllable test scheme, injects a
/// deterministic <see cref="FakeFhirDataSource"/> (no live siblings), and a capturing audit client so PHI-read /
/// create audit can be asserted. Most FHIR reads/searches/creates touch NO database (the façade owns none); only
/// the idempotency-ledger replay path needs <c>INTEROP_TEST_DB</c>, so a harmless dummy connection lets the host
/// boot for the DB-free tests.</summary>
public sealed class InteropFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("INTEROP_TEST_DB");
    public FakeFhirDataSource Source { get; } = new();
    public CapturingAuditClient Audit { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Dummy conn string when no test DB — the host boots; DB is only touched by the ledger path.
                ["ConnectionStrings:Interop"] = Db ?? "Host=localhost;Port=1;Database=x;Username=x;Password=x",
                ["Events:UseInMemoryOutbox"] = "true",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.RemoveAll<IFhirDataSource>();
            services.AddSingleton<IFhirDataSource>(Source);
            services.RemoveAll<IAuditClient>();
            services.AddSingleton<IAuditClient>(Audit);
        });
    }

    public HttpClient ClientFor(string role, params string[] scopes)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", $"user-{role}");
        c.DefaultRequestHeaders.Add("X-Test-Role", role);
        c.DefaultRequestHeaders.Add("X-Test-Scope", string.Join(' ', scopes));
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "t0");
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        return c;
    }
}

/// <summary>Deterministic data source: one known beneficiary + canned clinical rows; records write commands.</summary>
public sealed class FakeFhirDataSource : IFhirDataSource
{
    public const string PatientId = "MRS-M-1";
    public JsonObject? LastCreateCommand { get; private set; }
    public string LastCreateResource { get; private set; } = "";
    private int _seq;

    private static BeneficiarySource Ben() => new(PatientId,
        [new SourceIdentifier("NationalID", "29001011234567"), new SourceIdentifier("UNHCRNo", "C-42")],
        "Hassan", "Amal", new DateOnly(1990, 1, 1), "female",
        [new SourceTelecom("phone", "+20100000000", "mobile")], []);

    public Task<BeneficiarySource?> ReadPatientAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<BeneficiarySource?>(id == PatientId ? Ben() : null);

    public Task<IReadOnlyList<BeneficiarySource>> SearchPatientsAsync(string? identifier, string? name, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BeneficiarySource>>([Ben()]);

    public Task<CoverageSource?> ReadCoverageAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<CoverageSource?>(new CoverageSource(id, PatientId, "Active", "Mersal", "plan", "Gold", [], [new CoverageLimit("Outpatient", 10000m, 7500m)]));

    public Task<IReadOnlyList<CoverageSource>> SearchCoverageAsync(string patientId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CoverageSource>>([new CoverageSource("COV-1", patientId, "Active", "Mersal", "plan", "Gold", [], [])]);

    public Task<ServiceRequestSource?> ReadServiceRequestAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<ServiceRequestSource?>(new ServiceRequestSource(id, "Approved", "order", "laboratory",
            new CodedConcept("http://www.ama-assn.org/go/cpt", "80053", "Metabolic panel"), 1m, "each", PatientId, "PR-1", "ORG-1"));

    public Task<IReadOnlyList<ServiceRequestSource>> SearchServiceRequestsAsync(string patientId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ServiceRequestSource>>([]);

    public Task<MedicationRequestSource?> ReadMedicationRequestAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<MedicationRequestSource?>(new MedicationRequestSource(id, "Active",
            new CodedConcept("http://www.whocc.no/atc", "A10BA02", "Metformin"), "500mg BID", 30m, "tablet", PatientId, "PR-1"));

    public Task<IReadOnlyList<MedicationRequestSource>> SearchMedicationRequestsAsync(string patientId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MedicationRequestSource>>([]);

    public Task<DiagnosticReportSource?> ReadDiagnosticReportAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<DiagnosticReportSource?>(new DiagnosticReportSource(id, "Final",
            new CodedConcept("http://loinc.org", "24323-8", "Metabolic panel"), PatientId, "SR-1", DateTimeOffset.UtcNow, "application/pdf", "Result"));

    public Task<IReadOnlyList<DiagnosticReportSource>> SearchDiagnosticReportsAsync(string patientId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DiagnosticReportSource>>([]);

    public Task<EncounterSource?> ReadEncounterAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<EncounterSource?>(new EncounterSource(id, "Completed", "AMB", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, PatientId, "PR-1"));

    public Task<IReadOnlyList<EncounterSource>> SearchEncountersAsync(string patientId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EncounterSource>>([]);

    public Task<ConditionSource?> ReadConditionAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<ConditionSource?>(new ConditionSource(id, "Active",
            new CodedConcept("http://hl7.org/fhir/sid/icd-10", "E11.9", "Type 2 diabetes"), PatientId, "ENC-1", DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<ConditionSource>> SearchConditionsAsync(string patientId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConditionSource>>([new ConditionSource("D-1", "Active",
            new CodedConcept("http://hl7.org/fhir/sid/icd-10", "E11.9", "Type 2 diabetes"), patientId, "ENC-1", DateTimeOffset.UtcNow)]);

    public Task<ObservationSource?> ReadObservationAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<ObservationSource?>(new ObservationSource(id, "Final", "vital-signs",
            new CodedConcept("http://loinc.org", "8867-4", "Heart rate"), 72m, "beats/minute", "/min", PatientId, "ENC-1", DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<ObservationSource>> SearchObservationsAsync(string patientId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ObservationSource>>([]);

    public Task<AllergyIntoleranceSource?> ReadAllergyAsync(string id, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<AllergyIntoleranceSource?>(new AllergyIntoleranceSource(id,
            new CodedConcept("http://snomed.info/sct", "227493005", "Cashew nuts"), "High", "Anaphylaxis", PatientId));

    public Task<IReadOnlyList<AllergyIntoleranceSource>> SearchAllergiesAsync(string patientId, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AllergyIntoleranceSource>>([]);

    public Task<SiblingWriteResult> CreateAsync(string resourceType, JsonObject nativeCommand, string? bearer, string? idempotencyKey, CancellationToken ct = default)
    {
        LastCreateCommand = nativeCommand;
        LastCreateResource = resourceType;
        var id = $"{resourceType}-{System.Threading.Interlocked.Increment(ref _seq)}";
        return Task.FromResult(new SiblingWriteResult(201, id, $"{{\"id\":\"{id}\"}}"));
    }
}

/// <summary>Captures audit drafts so tests can assert the hash-chained event was emitted for each interaction.</summary>
public sealed class CapturingAuditClient : IAuditClient
{
    public ConcurrentQueue<AuditEventDraft> Events { get; } = new();
    public ValueTask EmitAsync(AuditEventDraft draft, CancellationToken ct = default)
    {
        Events.Enqueue(draft);
        return ValueTask.CompletedTask;
    }
    public IEnumerable<AuditEventDraft> Fhir => Events.Where(e => e.EntityType.StartsWith("fhir:", StringComparison.Ordinal));
}

/// <summary>Test auth handler — builds a principal from X-Test-* headers (sub / role / scope / tenant / mfa).</summary>
public sealed class TestAuthHandler(
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
            foreach (var r in role.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)) claims.Add(new Claim("role", r));
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) claims.Add(new Claim("tenant_id", tenant.ToString()));
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));

        // 21.4 — programme enablement. A tenant that exists is on its programmes (migration 0009/0015 backfill
        // every existing tenant ON), so the harness mirrors that by default; otherwise the gate this service now
        // applies would refuse every test in the file and the failure would look like an authorization bug.
        // `X-Test-Features` overrides it — pass an empty value to assert the gate REFUSES.
        if (Request.Headers.TryGetValue("X-Test-Features", out var features))
        {
            foreach (var f in features.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("features", f));
        }
        else
        {
            claims.Add(new Claim("features", Mersal.Authz.ProgramFeatures.Interop));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
