using Mersal.Admin.Api;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Data;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Time;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("admin-service");
// The admin overlay: Org Admin (tenant:own) + Super Admin (global) administer access, not content. Every admin
// action is Sensitive → the allow is audited (grants, revocations, config, review decisions, access-matrix reads).
builder.Services.AddHbmpAuthorization(AdminPolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<AdminDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddAdminInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AdminGate>();
builder.Services.AddScoped<RoleAdminService>();
builder.Services.AddScoped<AccessReviewService>();
builder.Services.AddScoped<PolicyConfigService>();
builder.Services.AddScoped<GovernanceService>();
builder.Services.AddScoped<BreakGlassAdminService>();
builder.Services.AddScoped<TenantAdminService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<BranchAssignmentService>();   // 14.2
builder.Services.AddScoped<PayerAssignmentService>();   // 19.5 — payer scope (design 38 §6)
builder.Services.AddScoped<ProgramAdminService>();      // 21.6 — programme enablement administration (design 40 §4)
builder.Services.AddScoped<TenantProgramStore>();       // 21.4 — the gate itself (features + live-counted caps)

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("admin-service"))
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
app.UseHbmpRls(); // 18.B2 — bind app.tenant_id / app.provider_id from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "admin-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapUsers();          // 8b.1 grant/revoke/de-provision + access matrix (SoD-checked, audited)
app.MapAccessReview();   // 8b.1 access-review campaigns (recertify/revoke/auto-expire)
app.MapPolicyConfig();   // 8b.1 session/device policy + staged policy proposals
app.MapGovernance();     // 8b.2 master-data versioning + template linter + system config
app.MapPlatform();       // 8b.3 tenant admin + break-glass lifecycle + governance dashboards
app.MapBranchAssignments(); // 14.2 staff↔branch assignment + active-branch context
app.MapPayerAssignments();  // 19.5 user↔payer restriction + GET /me/payers (read by IPayerDirectory)
app.MapPrograms();          // 21.6 programme enablement admin — features + live-counted caps (design 40 §4)

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
