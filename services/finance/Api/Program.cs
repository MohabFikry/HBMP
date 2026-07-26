using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Finance.Api;
using Mersal.Finance.Infrastructure;
using Mersal.Events;
using Mersal.Time;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("finance-service");
// finance-service authorizes with the finance overlay: Finance holds ONLY the finance actions, so a diagnosis/EMR
// read is default-denied (finance ≠ diagnosis). Settlement approve is SoD-split; exports are audited.
builder.Services.AddHbmpAuthorization(FinancePolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<FinanceDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddFinanceInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<FinanceGate>();
builder.Services.AddScoped<FinanceDeps>();

// Contract prices are READ from provider-service under the caller's bearer token (never duplicated / mutated here).
builder.Services.AddHttpClient<IContractPriceProvider, HttpContractPriceClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Siblings:Provider"] ?? "http://provider-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("finance-service"))
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
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "finance-service" })).AllowAnonymous();

app.MapFinance(); // phase 10.2 — utilization + settlements + summaries + audited exports + projection seam

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
