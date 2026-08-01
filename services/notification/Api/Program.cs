using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
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
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<NotificationDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddNotificationInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<NotificationGate>();

// THE FAN-OUT SUBSCRIPTION (US-072). Every routed domain event becomes a notification here.
//
// This replaced a registration-only consumer, which was the single delivery path this service had: the
// routing table, the templates and the escalation model covered thirteen event types and twelve of them had
// nothing feeding them. One consumer now serves them all, so a new notification is a publisher change and a
// template row.
//
// Registered unconditionally: the consumer degrades to a warning when the broker is absent (dev without
// RabbitMQ) rather than taking the inbox API down with it.
builder.Services.Configure<DomainEventOptions>(builder.Configuration.GetSection(DomainEventOptions.SectionName));
builder.Services.AddHostedService<DomainEventConsumer>();

// The other half of the fan-out. `EscalationService` has been complete since phase 8 and was constructed only
// by its tests, so every `EscalationDueAt` the dispatcher has ever stamped went by unread. See EscalationSweeper.
builder.Services.AddHostedService<EscalationSweeper>();

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("notification-service"))
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

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "notification-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapNotifications(); // phase 8.1 — inbox + delivery + mark-read + fan-out seam

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
