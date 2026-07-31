using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using Mersal.Inventory.Api;
using Mersal.Inventory.Infrastructure;
using Mersal.Time;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("inventory-service");
builder.Services.AddHbmpAuthorization();
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<InventoryDbContext>();
// AND THE RELAY. document-service shipped AddHbmpDurableOutbox WITHOUT this line, so its events were written
// durably and never delivered — a queue with a publisher and no postman. The two belong together and are
// deliberately adjacent here so the next reader sees a pair rather than a line.
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddInventoryInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

// 25.6 — branch reach. Every inventory endpoint is branch-checked: a coordinator sees their own clinic's
// stock, a clinics manager sees all six in ONE response (BranchSetScoped, 25.1). Same endpoints, no separate
// "manager" routes.
builder.Services.AddScoped<BranchScopeState>();
builder.Services.AddScoped<BranchReachGuard>();
builder.Services.AddHttpClient<IBranchDirectory, HttpBranchDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("inventory-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseHbmpTransportSecurity();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseHbmpRls();   // bind app.tenant_id GUC from the principal (RLS, ADR-0011)

// Resolve the active-branch context per request (design 37 §3, 42 §1). BranchScoped callers are narrowed to a
// validated active branch; BranchSetScoped callers carry their whole permitted set with the header acting as a
// filter; an X-Active-Branch outside the permitted set is refused 403 + audited. THE INVARIANT: never trust
// the header — always resolve it against the grants.
app.Use(async (ctx, next) =>
{
    var principal = ctx.RequestServices.GetRequiredService<IHbmpPrincipalAccessor>().Principal;
    if (principal is not null && ctx.Request.Path.StartsWithSegments("/api/v1"))
    {
        var header = ctx.Request.Headers[BranchHeaders.ActiveBranch].FirstOrDefault();
        var directory = ctx.RequestServices.GetRequiredService<IBranchDirectory>();
        var state = await BranchScopeResolver.ResolveAsync(principal, header, directory, ctx.RequestAborted);
        if (state.Denied)
        {
            var audit = ctx.RequestServices.GetRequiredService<IAuditClient>();
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "branch_scope", EntityId = header ?? "(none)", Action = AuditAction.Grant,
                ActorUserId = principal.Subject, TenantId = principal.TenantId, ActorMfa = principal.MfaSatisfied,
                DecisionOutcome = "BranchScopeDenied", DecisionReasonCode = "branch-not-permitted",
                Severity = AuditSeverity.High,
            }, ctx.RequestAborted);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { title = "branch-not-permitted", detail = "the requested active branch is not in your permitted set" });
            return;
        }
        ctx.RequestServices.GetRequiredService<BranchScopeState>().Context = state.Context;
        if (state.Context.ActiveBranchId is { } active) ctx.Response.Headers["X-Active-Branch"] = active.ToString();
    }
    await next();
});

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "inventory-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapInventory();
app.MapPrometheusScrapingEndpoint();   // /metrics — golden signals (Phase 11.3)

// `app.Run()`, not `await app.RunAsync()`, and the difference is not cosmetic: an async entry point makes
// Main return a Task, which the Swashbuckle CLI cannot host — it falls back to hunting for a `Startup` type,
// finds none, and the OpenAPI gate reports "generation failed" for a service that runs perfectly. Every other
// service on the platform ends this way for the same reason.
app.Run();

/// <summary>Exposed so the test host can boot the real pipeline.</summary>
public partial class Program;
