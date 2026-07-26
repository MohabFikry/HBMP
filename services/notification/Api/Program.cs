using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Notification.Api;
using Mersal.Notification.Infrastructure;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("notification-service");
// notification-service authorizes with the notification overlay: self-service inbox reads (row-filtered by
// recipient) + the system fan-out seam. Bodies carry no clinical payload; sensitive-context sends are audited.
builder.Services.AddHbmpAuthorization(NotificationPolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<NotificationDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddNotificationInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<NotificationGate>();

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("notification-service"))
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

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "notification-service" })).AllowAnonymous();

app.MapNotifications(); // phase 8.1 — inbox + delivery + mark-read + fan-out seam

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
