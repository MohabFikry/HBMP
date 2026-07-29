using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.CallCentre.Api;
using Mersal.CallCentre.Infrastructure;
using Mersal.Data;
using Mersal.Events;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("callcentre-service");
// callcentre authorizes with the call-centre overlay: coarse role+tenant scopes. The DEFINING control — "verify
// before you disclose" — is enforced by VerificationService on the disclose/act endpoints, not by the engine.
builder.Services.AddHbmpAuthorization(CallCentrePolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<CallCentreDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddCallCentreInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CallCentreGate>();
builder.Services.AddScoped<CallDeps>();

// 15.2 — the member view is COMPOSED from sibling services under the caller's bearer token (each enforces its own
// authorization; defense in depth). Named clients per sibling; base URLs from config. The 360 projection is
// clinical-free by construction (Member360 has no clinical field).
// 20.2 — the member 360 is now a PROJECTION OF THE ONE CANONICAL PROFILE (design 39 §2). Identity, coverage
// and open referrals come from profile-service; the call-centre ACTION affordances (appointments, contacts,
// follow-ups) still come from the services that own them, because the profile contract has no section for
// them. HttpMemberDirectory stays registered as the inner source for exactly those.
builder.Services.AddScoped<HttpMemberDirectory>();
builder.Services.AddScoped<Mersal.CallCentre.Infrastructure.IMemberDirectory, ProfileBackedMemberDirectory>();
// 15.3 — appointment actions delegate to the emr engine (no-double-book/idempotency/If-Match preserved there).
builder.Services.AddScoped<Mersal.CallCentre.Infrastructure.IAppointmentGateway, HttpAppointmentGateway>();
// 15.4 — contact corrections delegate to patient-service (one-primary rule + history live there).
builder.Services.AddScoped<Mersal.CallCentre.Infrastructure.IContactGateway, HttpContactGateway>();
foreach (var (name, url) in new[]
{
    ("eligibility", builder.Configuration["Siblings:Eligibility"] ?? "http://eligibility-service:8080"),
    ("emr", builder.Configuration["Siblings:Emr"] ?? "http://emr-service:8080"),
    ("patient", builder.Configuration["Siblings:Patient"] ?? "http://patient-service:8080"),
    ("pharmacy", builder.Configuration["Siblings:Pharmacy"] ?? "http://pharmacy-service:8080"),
    ("profile", builder.Configuration["Siblings:Profile"] ?? "http://profile-service:8080"),
})
{
    builder.Services.AddHttpClient(name, c => c.BaseAddress = new Uri(url));
}

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("callcentre-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    // Golden-signal metrics (latency/traffic/errors via ASP.NET Core; saturation via runtime),
    // exposed at /metrics for Prometheus scrape (Phase 11.3 observability, NFR-082).
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

// Enums travel as strings on the wire (the SPA sends "Inbound"/"BookAppointment"/…).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
// 21.4 — the THIRD gate, asked LAST: after authorization, before execution (design 40 §4). Placed here
// rather than on each route group because the contact centre is one programme, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.CallCentre);
app.UseHbmpRls(); // 18.B2 — bind app.tenant_id / app.provider_id from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "callcentre-service" })).AllowAnonymous();
app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals for Prometheus (in-cluster scrape only)

app.MapInteractions();   // phase 15.1 — call interactions + caller verification (the verification gate)
app.MapMembers();        // phase 15.2 — member search + minimum-necessary, clinical-free 360 (verification-gated)
app.MapCallAppointments(); // phase 15.3 — book/reschedule/cancel via the emr engine (verification-gated, linked)
app.MapContacts();       // phase 15.4 — contact corrections via patient-service (verification-gated, validated)
app.MapCallHistory();    // phase 20.3b — the member's call history, projected Full/Operational/Meta + copyText
app.MapKpis();           // phase 15.6 — PHI-free call-centre KPIs (supervisor/manager scope)

app.Run();

public partial class Program;
