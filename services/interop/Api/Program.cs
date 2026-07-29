using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Interop.Api;
using Mersal.Interop.Infrastructure;
using Mersal.Data;
using Mersal.Events;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Mersal.Time;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("interop-service");
// The FHIR façade authorizes with the interop overlay: role + SMART scope + tenant per FHIR interaction, run at
// the POLICY layer (every deny audited). Field/record-level ABAC is enforced by the owning service under the
// caller's bearer token (defense in depth) — the façade is never an authorization bypass.
builder.Services.AddHbmpAuthorization(InteropPolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration);
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<InteropDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddInteropInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InteropGate>();
builder.Services.AddScoped<FhirAudit>();
builder.Services.AddScoped<FhirDeps>();

// The façade reads/writes the internal model through the owning services' native /api/v1 endpoints, under the
// caller's bearer token. Named clients per sibling; base URLs from config.
foreach (var (name, url) in new[]
{
    ("patient", builder.Configuration["Siblings:Patient"] ?? "http://patient-service:8080"),
    ("eligibility", builder.Configuration["Siblings:Eligibility"] ?? "http://eligibility-service:8080"),
    ("orders", builder.Configuration["Siblings:Orders"] ?? "http://orders-service:8080"),
    ("pharmacy", builder.Configuration["Siblings:Pharmacy"] ?? "http://pharmacy-service:8080"),
    ("emr", builder.Configuration["Siblings:Emr"] ?? "http://emr-service:8080"),
})
{
    builder.Services.AddHttpClient(name, c => c.BaseAddress = new Uri(url));
}

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("interop-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseHbmpTransportSecurity();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
// 21.4 — the THIRD gate, asked LAST: after authorization, before execution (design 40 §4). Placed here
// rather than on each route group because FHIR/HL7 exchange is opt-in per organisation, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.Interop);
app.UseHbmpRls(); // 18.B2 — bind app.tenant_id / app.provider_id from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "interop-service" })).AllowAnonymous();
app.MapPrometheusScrapingEndpoint();

app.MapFhir();        // phase 13.1 — the FHIR R4 façade at /fhir/r4 (read/search all; safe creates → native)
app.MapIntegration(); // phase 13.2 — partner registry + DPIA-gated enablement + inbound anti-corruption ingest

app.Run();

public partial class Program;
