using Mersal.Approvals.Api;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
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
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<ApprovalsDbContext>();
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
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

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
// rather than on each route group because pre-authorization is one programme, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.Approvals);
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "approvals-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapWorklist();   // phase 7.1 ingestion + reviewer inbox + assign
app.MapReview();     // phase 7.1 clinical review view (field-scoped, PHI-read audited)
app.MapDecisions();  // phase 7.2 decisions (mandatory rationale) + downstream events + TAT/SLA
app.MapBreakGlass(); // phase 7.3 emergency / override / manual + retrospective queue + TAT summary
app.MapProfileAuthorizations(); // 20.2 — the profile's authorizations section
app.MapUtilizationFacts(); // 19.4 — raised/approved/denied COUNTS for utilization; no clinical payload

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
