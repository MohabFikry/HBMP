using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.CallCentre.Api;
using Mersal.CallCentre.Infrastructure;
using Mersal.Events;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("callcentre-service");
// callcentre authorizes with the call-centre overlay: coarse role+tenant scopes. The DEFINING control — "verify
// before you disclose" — is enforced by VerificationService on the disclose/act endpoints, not by the engine.
builder.Services.AddHbmpAuthorization(CallCentrePolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddCallCentreInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CallCentreGate>();
builder.Services.AddScoped<CallDeps>();

// 15.2 — the member view is COMPOSED from sibling services under the caller's bearer token (each enforces its own
// authorization; defense in depth). Named clients per sibling; base URLs from config. The 360 projection is
// clinical-free by construction (Member360 has no clinical field).
builder.Services.AddScoped<Mersal.CallCentre.Infrastructure.IMemberDirectory, HttpMemberDirectory>();
// 15.3 — appointment actions delegate to the emr engine (no-double-book/idempotency/If-Match preserved there).
builder.Services.AddScoped<Mersal.CallCentre.Infrastructure.IAppointmentGateway, HttpAppointmentGateway>();
foreach (var (name, url) in new[]
{
    ("eligibility", builder.Configuration["Siblings:Eligibility"] ?? "http://eligibility-service:8080"),
    ("emr", builder.Configuration["Siblings:Emr"] ?? "http://emr-service:8080"),
    ("patient", builder.Configuration["Siblings:Patient"] ?? "http://patient-service:8080"),
    ("pharmacy", builder.Configuration["Siblings:Pharmacy"] ?? "http://pharmacy-service:8080"),
})
{
    builder.Services.AddHttpClient(name, c => c.BaseAddress = new Uri(url));
}

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("callcentre-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter());

// Enums travel as strings on the wire (the SPA sends "Inbound"/"BookAppointment"/…).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "callcentre-service" })).AllowAnonymous();

app.MapInteractions();   // phase 15.1 — call interactions + caller verification (the verification gate)
app.MapMembers();        // phase 15.2 — member search + minimum-necessary, clinical-free 360 (verification-gated)
app.MapCallAppointments(); // phase 15.3 — book/reschedule/cancel via the emr engine (verification-gated, linked)

app.Run();

public partial class Program;
