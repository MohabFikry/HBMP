using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Pharmacy.Api;
using Mersal.Pharmacy.Infrastructure;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var masterDataUrl = builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080";
var emrUrl = builder.Configuration["Emr:BaseUrl"] ?? "http://emr-service:8080";

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("pharmacy-service");
// Pharmacy authorizes with the pharmacy overlay: treating-relationship on prescribe/refer (+ provider PO phase-6).
builder.Services.AddHbmpAuthorization(PharmacyPolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddPharmacyInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<PharmacyGate>();

// Named clients for the advisory screener (masterdata interactions/allergies + emr allergy list).
builder.Services.AddHttpClient("masterdata", c => c.BaseAddress = new Uri(masterDataUrl));
builder.Services.AddHttpClient("emr", c => c.BaseAddress = new Uri(emrUrl));
builder.Services.AddScoped<IPrescribingScreener, HttpPrescribingScreener>();
// Typed clients: drug validation (masterdata) + treating relationship (emr) — both fail-closed on writes.
builder.Services.AddHttpClient<IDrugValidator, HttpDrugValidator>(c => c.BaseAddress = new Uri(masterDataUrl));
builder.Services.AddHttpClient<ITreatingRelationshipClient, HttpTreatingRelationshipClient>(c => c.BaseAddress = new Uri(emrUrl));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("pharmacy-service"))
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

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "pharmacy-service" })).AllowAnonymous();

app.MapPrescriptions();
app.MapReferrals();

app.Run();

public partial class Program;
