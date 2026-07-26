using Mersal.Admin.Api;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
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

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("admin-service"))
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
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "admin-service" })).AllowAnonymous();

app.MapUsers();          // 8b.1 grant/revoke/de-provision + access matrix (SoD-checked, audited)
app.MapAccessReview();   // 8b.1 access-review campaigns (recertify/revoke/auto-expire)
app.MapPolicyConfig();   // 8b.1 session/device policy + staged policy proposals
app.MapGovernance();     // 8b.2 master-data versioning + template linter + system config
app.MapPlatform();       // 8b.3 tenant admin + break-glass lifecycle + governance dashboards
app.MapBranchAssignments(); // 14.2 staff↔branch assignment + active-branch context

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
