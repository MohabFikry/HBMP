using System.Text.Json.Serialization;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Profile.Api;
using Mersal.Profile.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// profile-service — the unified patient profile (phase 20, design 39).
//
// A COMPOSITION service. It has no DbContext, no migrations and no schema: design 39 §7.4 makes "owns no data"
// an invariant, because a local copy of clinical data here would be a second source of truth for the record a
// clinician makes decisions from. That is also why this service is on the RLS exemption list in
// libs/architecture — there is no connection to bind a tenant GUC on, because there is no database.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("profile-service");
// The profile overlay: coarse role+scope+tenant on the aggregate. The section matrix (design 39 §4) is the
// second, finer layer and lives in the composer — one gate could not express "visible, but only your own rows".
builder.Services.AddHbmpAuthorization(ProfilePolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // break-glass works, but loudly (design 39 §7.6)
// Audit goes STRAIGHT to the broker, not through a transactional outbox — because there is no transaction to
// be atomic with. The outbox exists so a state change cannot exist without its audit row; this service makes
// no state change. Staging audit rows in a database would mean giving a service that owns no data a database
// purely to hold them. The sink fails the request rather than completing a PHI read unaudited.
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddHbmpDirectAuditSink();
builder.Services.AddProfileComposition(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ProfileDeps>();

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("profile-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // THE INVARIANT, in one line (design 39 §7.1): a withheld field is null, and a null field is not written.
    // Without this the payload would carry `"data": null` for every restricted section — harmless in itself,
    // but it is the same setting that keeps a projected-away property from being serialized as an empty value.
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseHbmpTransportSecurity();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "profile-service" })).AllowAnonymous();
app.MapPrometheusScrapingEndpoint();

app.MapProfile();  // 20.1 — GET /patients/{id}/profile (+ /profile/summary, the audited PHI export)
app.MapPhoto();    // 20.3 — GET /patients/{id}/photo, consent-gated upstream, short-TTL signed, audited

app.Run();

public partial class Program;
