using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Case.Api;
using Mersal.Case.Infrastructure;
using Mersal.Events;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("case-service");
// case-service authorizes with the case overlay: the case-assignment ABAC condition scopes every case/360 action
// to an active assignment (10 §3.11); assign/unassign is supervisory; 360 assembly is a PHI-read (audited).
builder.Services.AddHbmpAuthorization(CasePolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddCaseInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CaseGate>();
builder.Services.AddScoped<CaseDeps>();

// The beneficiary-360 coordination view is assembled from sibling services under the caller's bearer token (each
// enforces its own authorization — defense in depth). Named clients per sibling; base URLs from config.
builder.Services.AddScoped<IBeneficiary360Assembler, HttpBeneficiary360Assembler>();
foreach (var (name, url) in new[]
{
    ("eligibility", builder.Configuration["Siblings:Eligibility"] ?? "http://eligibility-service:8080"),
    ("approvals", builder.Configuration["Siblings:Approvals"] ?? "http://approvals-service:8080"),
    ("appointments", builder.Configuration["Siblings:Appointments"] ?? "http://appointment-service:8080"),
    ("emr", builder.Configuration["Siblings:Emr"] ?? "http://emr-service:8080"),
})
{
    builder.Services.AddHttpClient(name, c => c.BaseAddress = new Uri(url));
}

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("case-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "case-service" })).AllowAnonymous();

app.MapCases();           // phase 10.1 — case CRUD + My Cases + assign/unassign + tasks + escalations
app.MapBeneficiary360();  // phase 10.1 — coordination-360 (field-scoped, PHI-read audited) + eligibility override

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
