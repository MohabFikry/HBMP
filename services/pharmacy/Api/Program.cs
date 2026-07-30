using System.Text.Json.Serialization;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using Mersal.Pharmacy.Api;
using Mersal.Pharmacy.Infrastructure;
using Mersal.Time;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var masterDataUrl = builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080";
var emrUrl = builder.Configuration["Emr:BaseUrl"] ?? "http://emr-service:8080";
var patientUrl = builder.Configuration["Patient:BaseUrl"] ?? "http://patient-service:8080";

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

// Named clients for the advisory screener (masterdata interactions/allergies + emr allergy list) + phase-6 lookups.
builder.Services.AddHttpClient("masterdata", c => c.BaseAddress = new Uri(masterDataUrl));
builder.Services.AddHttpClient("emr", c => c.BaseAddress = new Uri(emrUrl));
builder.Services.AddHttpClient("patient", c => c.BaseAddress = new Uri(patientUrl));
builder.Services.AddScoped<IPrescribingScreener, HttpPrescribingScreener>();
// Phase 6.3 formulary/PBM stand-in (masterdata approved-alternatives) + phase-6.1 beneficiary resolver (patient-service).
builder.Services.AddScoped<IFormularyService, MasterDataFormularyService>();
builder.Services.AddScoped<IBeneficiaryResolver, HttpBeneficiaryResolver>();
// Typed clients: drug validation (masterdata) + treating relationship (emr) — both fail-closed on writes.
builder.Services.AddHttpClient<IDrugValidator, HttpDrugValidator>(c => c.BaseAddress = new Uri(masterDataUrl));
builder.Services.AddHttpClient<ITreatingRelationshipClient, HttpTreatingRelationshipClient>(c => c.BaseAddress = new Uri(emrUrl));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("pharmacy-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

// Accept enum names in request bodies (matches the string enums we emit on responses); numeric values still work.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
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
app.MapDispensing();
app.MapReferrals();
app.MapProfileSections();  // 20.2 — the profile's prescriptions + referrals sections (provider-ownership here)

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
