using System.Text.Json.Serialization;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Claims.Api;
using Mersal.Claims.Infrastructure;
using Mersal.Events;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("claims-service");
// claims-service authorizes with the claims overlay: the claims roles hold ONLY the claims actions, so a
// diagnosis/EMR read is default-denied (claims ≠ diagnosis). SoD + dual control are enforced in the handlers.
builder.Services.AddHbmpAuthorization(ClaimsPolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddClaimsInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ClaimsGate>();
builder.Services.AddScoped<ClaimsDeps>();

// Contract tariffs are READ from provider-service under the caller's bearer token (never duplicated / mutated here).
builder.Services.AddHttpClient<IContractTariffProvider, HttpContractTariffClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Siblings:Provider"] ?? "http://provider-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("claims-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "claims-service" })).AllowAnonymous();

app.MapClaims();    // phase 10b.1 — auto-derived claims + min-necessary reads + intake seam
app.MapBatches();   // phase 10b.2 — batching + batch lifecycle (single-open-batch DB guard)
app.MapDecisions(); // phase 10b.4 — officer worklist + line decisions (SoD + dual control)
app.MapSubmissions(); // phase 10b.5 — provider-submitted claims + document matching
app.MapReimbursements(); // phase 10b.6 — beneficiary reimbursement + OCR (assistive, human-gated)
app.MapReconciliation(); // phase 10b.7 — reconciliation worklist + append-only adjustments
app.MapSettlement(); // phase 10b.8 — settlement advice + exports (NO payment execution)
app.MapAppeals(); // phase 10b.9 — appeals (preserve decision thread) + claims KPI feed

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
