using System.Text.Json.Serialization;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using Mersal.Orders.Api;
using Mersal.Orders.Infrastructure;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("orders-service");
// Orders authorizes with the order overlay: treating-relationship on create/read (+ provider PO for phase-5 reads).
builder.Services.AddHbmpAuthorization(OrdersPolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<OrdersDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddOrdersInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<OrdersGate>();
builder.Services.AddScoped<FulfillmentGate>();

// Line codes validated against masterdata; treating relationship verified via emr-service (both fail-closed).
builder.Services.AddHttpClient<ICodeValidator, HttpCodeValidator>(c =>
    c.BaseAddress = new Uri(builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080"));
// 14.6 — examination-type sensitivity resolved + pinned at order creation (fail-closed).
builder.Services.AddHttpClient<IExaminationTypeResolver, HttpExaminationTypeResolver>(c =>
    c.BaseAddress = new Uri(builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080"));
builder.Services.AddHttpClient<ITreatingRelationshipClient, HttpTreatingRelationshipClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Emr:BaseUrl"] ?? "http://emr-service:8080"));
// Result reports are stored in document-service (scanned, CMK blob); we keep only the returned blob ref.
builder.Services.AddHttpClient<IReportDocumentClient, HttpReportDocumentClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Document:BaseUrl"] ?? "http://document-service:8080"));

// 14.4 — active-branch context for the clinician-side order worklist (permitted set from admin-service).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<BranchScopeState>();
builder.Services.AddHttpClient<IBranchDirectory, HttpBranchDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("orders-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

// Accept enum names (e.g. "Lab", "LOINC") in request bodies — matching the string enums we already emit on
// responses. JsonStringEnumConverter still accepts numeric values too, so this is backward compatible.
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
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

// 14.4 — resolve active-branch context (design 37 §3): BranchScoped clinicians are narrowed to a validated
// active branch; an out-of-set X-Active-Branch is 403 + audited. The provider queue is unaffected (its
// callers are provider-scoped → branch-unrestricted).
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
                DecisionOutcome = "BranchScopeDenied", DecisionReasonCode = "branch-not-permitted", Severity = AuditSeverity.High,
            }, ctx.RequestAborted);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { title = "branch-not-permitted" });
            return;
        }
        ctx.RequestServices.GetRequiredService<BranchScopeState>().Context = state.Context;
    }
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "orders-service" })).AllowAnonymous();

app.MapOrders();
app.MapQueue();      // phase 5.1 provider queue + search
app.MapConsume();    // phase 5.2 atomic idempotent consume
app.MapResults();    // phase 5.3 result upload + routing
app.MapReportAccess(); // phase 14.7 sensitive-result release requests + grants

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
