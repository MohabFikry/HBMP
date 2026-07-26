using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.CallCentre.Api;
using Mersal.CallCentre.Infrastructure;
using Mersal.Events;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("callcentre-service");
// callcentre authorizes with the call-centre overlay: coarse role+tenant scopes. The DEFINING control — "verify
// before you disclose" — is enforced by VerificationService on the disclose/act endpoints, not by the engine.
builder.Services.AddHbmpAuthorization(CallCentrePolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddCallCentreInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CallCentreGate>();
builder.Services.AddScoped<CallDeps>();

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("callcentre-service"))
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

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "callcentre-service" })).AllowAnonymous();

app.MapInteractions();   // phase 15.1 — call interactions + caller verification (the verification gate)

app.Run();

public partial class Program;
