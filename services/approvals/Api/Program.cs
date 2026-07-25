using Mersal.Approvals.Api;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Accept enum NAMES in request bodies (e.g. decision "Approved") as well as numbers — the portals send
// readable enum strings. Backward compatible: numeric enum values still deserialize.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("approvals-service");
// Approvals authorizes with the approvals overlay: tenant-scoped oversight reads (no treating relationship);
// review/decision/break-glass actions are flagged sensitive → PHI-read/decision audit.
builder.Services.AddHbmpAuthorization(ApprovalsPolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddApprovalsInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ApprovalsGate>();
builder.Services.AddScoped<DecisionDeps>();

// The clinical review view assembles a field-scoped projection from emr-service under the caller's purpose (PUR),
// fail-closed. document-service supplies supporting reports; both are reached with the caller's bearer token.
builder.Services.AddHttpClient<IClinicalContextProvider, HttpClinicalContextClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Emr:BaseUrl"] ?? "http://emr-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("approvals-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "approvals-service" })).AllowAnonymous();

app.MapWorklist();   // phase 7.1 ingestion + reviewer inbox + assign
app.MapReview();     // phase 7.1 clinical review view (field-scoped, PHI-read audited)
app.MapDecisions();  // phase 7.2 decisions (mandatory rationale) + downstream events + TAT/SLA
app.MapBreakGlass(); // phase 7.3 emergency / override / manual + retrospective queue + TAT summary

app.Run();

public partial class Program;
