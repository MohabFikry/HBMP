using System.Text.Json.Serialization;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using Mersal.Pharmacy.Api;
using Mersal.Pharmacy.Infrastructure;
using Mersal.Time;
using Mersal.Validity;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Mersal.BeneficiaryLookup;

var builder = WebApplication.CreateBuilder(args);

var masterDataUrl = builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080";
var emrUrl = builder.Configuration["Emr:BaseUrl"] ?? "http://emr-service:8080";
var patientUrl = builder.Configuration["Patient:BaseUrl"] ?? "http://patient-service:8080";
var adminUrl = builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080";
var eligibilityUrl = builder.Configuration["Eligibility:BaseUrl"] ?? "http://eligibility-service:8080";

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("pharmacy-service");
// Pharmacy authorizes with the pharmacy overlay: treating-relationship on prescribe/refer (+ provider PO phase-6).
builder.Services.AddHbmpAuthorization(PharmacyPolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<PharmacyDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddPharmacyInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<PharmacyGate>();
builder.Services.AddScoped<DispensingGate>();
builder.Services.AddScoped<DispenseExecutor>();
builder.Services.AddScoped<AmendExecutor>();
builder.Services.AddScoped<ChronicAmendExecutor>();   // 30.3 — chronic duration/frequency amendment   // 30.2 — the guarded cancel/amend transition
// Gives expiry a MOMENT: without it a lapsed prescription reads "Approved" for ever in the row, the reports
// drawn from it and the audit trail, even though no counter can dispense it.
builder.Services.AddHostedService<PrescriptionExpirySweeper>();

// Named clients for the advisory screener (masterdata interactions/allergies + emr allergy list) + phase-6 lookups.
builder.Services.AddHttpClient("masterdata", c => c.BaseAddress = new Uri(masterDataUrl));
builder.Services.AddHttpClient("emr", c => c.BaseAddress = new Uri(emrUrl));
builder.Services.AddHttpClient("patient", c => c.BaseAddress = new Uri(patientUrl));
// The cost-share quote for the dispensing counter. pharmacy contributes what the medicines cost; the
// member/payer SPLIT comes from eligibility, which composes it through libs/benefit-pricing — the same path
// claims adjudicates with, so the counter and the claim cannot quote different numbers.
builder.Services.AddHttpClient("eligibility", c => c.BaseAddress = new Uri(eligibilityUrl));
// How long a prescription stays dispensable, set by clinical governance in admin-service. Read with the
// PRESCRIBER's token — the endpoint is authenticated-only and discloses four integers, no patient data.
builder.Services.AddHttpClient<IValidityPolicySource, HttpValidityPolicySource>(c => c.BaseAddress = new Uri(adminUrl));
builder.Services.AddScoped<IBenefitPreCheck, NotYetImplementedBenefitPreCheck>();
// 29.2 — the CPT vehicle behind a referral, resolved by masterdata rather than re-derived here.
builder.Services.AddScoped<IReferralServiceResolver, HttpReferralServiceResolver>();

// Manufacturer labels for the live interaction and dosing checks. The ONLY third-party call in the
// prescribing path: a public U.S. FDA endpoint that receives an active-ingredient name and nothing else.
// The base address is configurable so an air-gapped or offline deployment can point it at a mirror — and so
// the tests can point it at a stub rather than the internet.
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("openfda", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["OpenFda:BaseUrl"] ?? "https://api.fda.gov");
    // Shorter than the pass budget, so one slow label cannot consume the whole allowance. Not shorter than
    // openFDA actually takes, though: at 4s every cold-start lookup timed out, because the first call from a
    // fresh container pays DNS and a TLS handshake on top of the ~3s the API itself needs.
    c.Timeout = TimeSpan.FromSeconds(9);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MersalHBMP/1.0 (+https://mersal.foundation)");
});
builder.Services.AddScoped<IDrugLabelSource, OpenFdaLabelSource>();
builder.Services.AddScoped<IPrescribedMedicationSource, DbPrescribedMedicationSource>();
builder.Services.AddScoped<IClinicalValidationPorts, HttpClinicalValidationPorts>();
builder.Services.AddScoped<IPrescribingScreener, ValidatorBackedPrescribingScreener>();
builder.Services.AddScoped<PrescriptionValidationService>();
// Phase 6.3 formulary/PBM stand-in (masterdata approved-alternatives) + phase-6.1 beneficiary resolver (patient-service).
builder.Services.AddScoped<IFormularyService, MasterDataFormularyService>();
builder.Services.AddHbmpBeneficiaryLookup();  // shared with orders — see libs/beneficiary-lookup
// Typed clients: drug validation (masterdata) + treating relationship (emr) — both fail-closed on writes.
builder.Services.AddHttpClient<IDrugValidator, HttpDrugValidator>(c => c.BaseAddress = new Uri(masterDataUrl));
builder.Services.AddHttpClient<ITreatingRelationshipClient, HttpTreatingRelationshipClient>(c => c.BaseAddress = new Uri(emrUrl));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("pharmacy-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

// Accept enum names in request bodies (matches the string enums we emit on responses); numeric values still work.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// 29.5 — records the forfeiture of refill windows that closed uncollected (design 45 §5). It RECORDS; the
// counter already ENFORCES, so a stall here delays a status, never a patient.
builder.Services.AddHostedService<RefillWindowSweeper>();

// THE RETURN LEG OF THE PRIOR-AUTHORIZATION SAGA (23 §3), and the sharpest gap it closes: `IsDispensable`
// admits only Approved and PartiallyDispensed, and the only path that ever set a prescription Approved was
// the auto-route at creation — so a script that WAS sent for approval could never be dispensed, whatever the
// reviewer decided. Its own queue, because the transport is point-to-point and orders needs the same
// decisions.
builder.Services.Configure<ApprovalDecisionConsumerOptions>(builder.Configuration.GetSection(ApprovalDecisionConsumerOptions.SectionName));
builder.Services.AddScoped<PrescriptionApprovalApplier>();
builder.Services.AddHostedService<ApprovalDecisionConsumer>();

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Readiness for the probe in infra/helm/rollout/rollout-template.yaml. Process-level only: this reports
// "through startup and able to serve". A dependency check here would pull the pod out of rotation for a
// condition the service already surfaces per-request, turning a partial degradation into a total outage.
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
// 21.4 — the THIRD gate, asked LAST: after authorization, before execution (design 40 §4). Placed here
// rather than on each route group because dispensing + referrals are the pharmacy programme, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.Pharmacy);
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "pharmacy-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapPrescriptions();
app.MapRxPricing();     // what the prescription costs, and the member/payer split from eligibility
app.MapDispensing();
app.MapRxAmendment();  // 30.2 — cancel/amend a signed prescription at LINE level (design 46 §1-§3)
app.MapExtendValidity();   // approvals calls this when a validity-extension request is approved
app.MapReferrals();
app.MapProfileSections();  // 20.2 — the profile's prescriptions + referrals sections (provider-ownership here)

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
