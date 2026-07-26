using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Reporting.Api;
using Mersal.Reporting.Infrastructure;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("reporting-service");
// reporting-service authorizes with the reporting overlay: zone-split reads (operational / clinical-coded /
// financial) so finance ≠ diagnosis is enforced in authz; a system projection seam; audited exports.
builder.Services.AddHbmpAuthorization(ReportingPolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<ReportingDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddReportingInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ReportingGate>();
builder.Services.AddScoped<ReportContext>();
builder.Services.AddScoped<Mersal.Reporting.Infrastructure.DashboardBuilder>();

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("reporting-service"))
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

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "reporting-service" })).AllowAnonymous();

app.MapReports(); // phase 8.2 — KPI read-model APIs + projection seam + audited export

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
