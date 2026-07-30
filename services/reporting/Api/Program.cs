using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using Mersal.Reporting.Api;
using Mersal.Reporting.Infrastructure;
using Mersal.Time;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("reporting-service");
// reporting-service authorizes with the reporting overlay: zone-split reads (operational / clinical-coded /
// financial) so finance ≠ diagnosis is enforced in authz; a system projection seam; audited exports.
builder.Services.AddHbmpAuthorization(ReportingPolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<ReportingDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddReportingInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ReportingGate>();
builder.Services.AddScoped<ReportContext>();
builder.Services.AddScoped<Mersal.Reporting.Infrastructure.DashboardBuilder>();
builder.Services.AddScoped<AnalyticsContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
// 19.6b — payer scope. The token contract is frozen and carries no payer claim, so the restriction is resolved
// per request from admin-service, exactly as policy-service does. It FAILS CLOSED to "restricted to nothing",
// because payer scope's empty set means UNRESTRICTED and an outage must never widen a dashboard's aggregate.
builder.Services.AddHttpClient<IPayerDirectory, ReportingPayerDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("reporting-service"))
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
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "reporting-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapReports(); // phase 8.2 — KPI read-model APIs + projection seam + audited export
app.MapAnalytics(); // phase 19.6b — the policy & member analytical dashboard (6 views, drill-down, export)

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
