using Mersal.BeneficiaryLookup;
using System.Text.Json.Serialization;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using Mersal.Orders.Api;
using Mersal.Orders.Infrastructure;
using Mersal.Validity;
using Mersal.Time;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
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
// 29.2b — the external delivering provider's gate. Row-scoped, NOT caller-scoped (design 45 §2b).
builder.Services.AddScoped<ProcedureProviderGate>();

// Line codes validated against masterdata; treating relationship verified via emr-service (both fail-closed).
builder.Services.AddHttpClient<ICodeValidator, HttpCodeValidator>(c =>
    c.BaseAddress = new Uri(builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080"));
// 14.6 — examination-type sensitivity resolved + pinned at order creation (fail-closed).
builder.Services.AddHttpClient<IExaminationTypeResolver, HttpExaminationTypeResolver>(c =>
    c.BaseAddress = new Uri(builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080"));
// 29.2 — the OP-Procedure type and the code's CPT section, both owned by masterdata (design 45 §2).
// Fail-closed: unreachable resolves the same as unknown, and the write path refuses rather than storing a
// type nobody validated.
builder.Services.AddHttpClient<IProcedureTypeResolver, HttpProcedureTypeResolver>(c =>
    c.BaseAddress = new Uri(builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080"));
// ADR-0034 — the bench's cost quote. The catalogue supplies what an examination costs; eligibility supplies
// the member/payer split through libs/benefit-pricing, the SAME path claims adjudicates with. Named clients
// rather than typed ones because OrderPricing composes two calls and owns neither contract.
builder.Services.AddHttpClient("masterdata", c =>
    c.BaseAddress = new Uri(builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080"));
// 27.8 — the bench's member search resolves identifiers through patient-service, exactly as the dispensing
// counter does. A NAMED client because the shared resolver asks the factory for "patient" by name; without
// this registration it would get a client with no BaseAddress and throw on a relative URL — an exception the
// resolver deliberately does not catch, because a misconfigured host is not a fail-safe "member not found".
builder.Services.AddHttpClient("patient", c =>
    c.BaseAddress = new Uri(builder.Configuration["Patient:BaseUrl"] ?? "http://patient-service:8080"));
builder.Services.AddHttpClient("eligibility", c =>
    c.BaseAddress = new Uri(builder.Configuration["Eligibility:BaseUrl"] ?? "http://eligibility-service:8080"));
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
// How long a lab / imaging / procedure order stays actionable, set by clinical governance in admin-service.
// Read with the ORDERING CLINICIAN's token: the endpoint is authenticated-only and discloses four integers.
builder.Services.AddHttpClient<IValidityPolicySource, HttpValidityPolicySource>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("orders-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

// Accept enum names (e.g. "Lab", "LOINC") in request bodies — matching the string enums we already emit on
// responses. JsonStringEnumConverter still accepts numeric values too, so this is backward compatible.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// 18.C2 (audit R2 W4) — the report-access expiry sweep on a timer. The endpoint existed and nothing called
// it, so grants stayed Active for ever: the read path filtered them out, but the grant list shown to a
// patient or the DPO said people still held access they had lost, and the expiry was never audited.
builder.Services.AddHostedService<ReportAccessExpirySweeper>();
// Lab / imaging / procedure orders lapse the same way prescriptions do — see OrderExpirySweeper.
builder.Services.AddHostedService<OrderExpirySweeper>();

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Readiness for the probe in infra/helm/rollout/rollout-template.yaml. Process-level only: this reports
// "through startup and able to serve". A dependency check here would pull the pod out of rotation for a
// condition the service already surfaces per-request, turning a partial degradation into a total outage.
builder.Services.AddHealthChecks();

builder.Services.AddHbmpBeneficiaryLookup();  // 27.8 — the bench searches the way the counter does

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
// 21.4 — the THIRD gate, asked LAST: after authorization, before execution (design 40 §4). Placed here
// rather than on each route group because investigation orders + results are one programme, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.Orders);
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
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapOrders();
app.MapValidateOrder();   // step 1 — advisory checks while composing (the ordering workspace)
app.MapQueue();      // phase 5.1 provider queue + search
app.MapProcedureProvider();   // 29.2b external delivering provider portal (design 45 §2b)
app.MapServiceHistory();      // 29.4 one service-history endpoint for every tab (design 45 §4)
app.MapConsume();    // phase 5.2 atomic idempotent consume
app.MapAmendment();  // 30.2 — cancel/amend a signed order at LINE level (design 46 §1-§3)
app.MapOrderPricing(); // ADR-0034 — what the order costs and how it splits (never a zero for an unknown)
app.MapExtendValidity();   // approvals calls this when a validity-extension request is approved
app.MapResults();    // phase 5.3 result upload + routing
app.MapReportAccess(); // phase 14.7 sensitive-result release requests + grants
app.MapProfileInvestigations(); // 20.2 — the profile's investigations section, sensitivity-gated PER LINE here

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
