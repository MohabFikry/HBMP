using Mersal.BenefitPricing;
using System.Text.Json.Serialization;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Claims.Api;
using Mersal.Claims.Infrastructure;
using Mersal.Data;
using Mersal.Events;
using Mersal.Time;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
// 19.1b — the shared tier-pricing path (same composition and same libs/money split as eligibility).
builder.Services.AddHbmpTierPricing(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("claims-service");
// claims-service authorizes with the claims overlay: the claims roles hold ONLY the claims actions, so a
// diagnosis/EMR read is default-denied (claims ≠ diagnosis). SoD + dual control are enforced in the handlers.
builder.Services.AddHbmpAuthorization(ClaimsPolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<ClaimsDbContext>();
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
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
// 21.4 — the THIRD gate, asked LAST: after authorization, before execution (design 40 §4). Placed here
// rather than on each route group because claims-service IS the claims programme, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.Claims);
app.UseHbmpRls(); // 18.B2 — bind app.tenant_id / app.provider_id from the principal (RLS, ADR-0011)
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
app.MapUtilizationFacts(); // 19.4 — claimed/approved/member-share TOTALS for utilization (no claim lines)

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
