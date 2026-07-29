using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
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
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<CaseDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddCaseInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CaseGate>();
builder.Services.AddScoped<CaseDeps>();

// 20.2 — the beneficiary-360 is now a PROJECTION OF THE ONE CANONICAL PROFILE, not a second fan-out. It calls
// profile-service under the caller's own bearer, so profile-service authorizes it and every owning service
// authorizes profile-service's onward call. Design 39 §2: a fifth aggregate would guarantee drift, and this
// service used to be the second one.
builder.Services.AddScoped<IBeneficiary360Assembler, ProfileBackedBeneficiary360Assembler>();
builder.Services.AddHttpClient(
    "profile",
    c => c.BaseAddress = new Uri(builder.Configuration["Siblings:Profile"] ?? "http://profile-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("case-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

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
// rather than on each route group because case management is one programme, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.CaseManagement);
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "case-service" })).AllowAnonymous();

app.MapCases();           // phase 10.1 — case CRUD + My Cases + assign/unassign + tasks + escalations
app.MapBeneficiary360();  // phase 10.1 — coordination-360 (field-scoped, PHI-read audited) + eligibility override
app.MapProfileCases();    // 20.2 — the profile's caseManagement section + the assignment fact

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
