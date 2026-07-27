using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.BenefitPricing;
using Mersal.Data;
using Mersal.Events;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using PolicyEntity = Mersal.Policy.Domain.Policy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("policy-service");
// 19.1 — policy-service now authorizes with its own bundle: authoring benefit configuration (policy:admin)
// is a different capability from administering a member against it (policy:write).
builder.Services.AddHbmpAuthorization(bundle: PolicyPolicies.Bundle());
builder.Services.AddScoped<PolicyGate>();
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<PolicyDbContext>();
builder.Services.AddHbmpOutboxRelay();   // relay staged events (incl. audit) to RabbitMQ
builder.Services.AddPolicyInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
// 19.1b — the tier catalogue, read from provider-service. policy administration PRICES tiers; the Network Team
// creates them, so this is deliberately a read-only window and not a local copy of the network model.
builder.Services.AddHttpClient<INetworkTierCatalog, HttpNetworkTierCatalog>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Provider:BaseUrl"] ?? "http://provider-service:8080"));
// 19.2 — enrolment validates the beneficiary against patient-service (never fail-soft: not knowing whether
// someone is Active is not a reason to enrol them) and issues the member number.
builder.Services.AddHttpClient<IBeneficiaryStatusProbe, HttpBeneficiaryStatusProbe>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Patient:BaseUrl"] ?? "http://patient-service:8080"));
builder.Services.AddScoped<IMemberNoIssuer, SequentialMemberNoIssuer>();
// 19.3b — documents. The bytes, the ClamAV scan and MinIO stay in document-service; policy-service adds only
// the linkage and the classification. OCR is a WIRED SEAM, disabled: extraction becomes authoritative-looking
// the moment it renders beside a real value, so it lands assistive and human-gated or not at all.
builder.Services.AddHttpClient<IDocumentStore, HttpDocumentStore>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Document:BaseUrl"] ?? "http://document-service:8080"));
builder.Services.AddScoped<IPolicyDocumentOcr, DisabledPolicyDocumentOcr>();
// 19.3c — the timeline projector. Nothing in the domain calls it as part of doing its work; it consumes
// events that already exist, which is what keeps the timeline from drifting into a second log.
builder.Services.AddScoped<TimelineProjector>();
// 19.4 — utilization. A DIRECT QUERY over the accumulator, not a projection (ADR-0023): reconciliation to
// coverage_limit has to be a property that cannot become false, not one somebody keeps true.
builder.Services.AddScoped<UtilizationQuery>();
builder.Services.AddScoped<UtilizationFactComposer>();
// The SAME tier resolver eligibility/approvals/claims price with, so a report cannot disagree with the money.
builder.Services.AddHttpClient<INetworkTierResolver, HttpNetworkTierResolver>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Provider:BaseUrl"] ?? "http://provider-service:8080"));
// The three facts a utilization report needs and policy-service does not own. Each fails SEPARATELY: an
// approvals outage must not blank the claim value too.
builder.Services.AddHttpClient<IEncounterFactSource, HttpEncounterFactSource>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Emr:BaseUrl"] ?? "http://emr-service:8080"));
builder.Services.AddHttpClient<IAuthorizationFactSource, HttpAuthorizationFactSource>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Approvals:BaseUrl"] ?? "http://approvals-service:8080"));
builder.Services.AddHttpClient<IClaimFactSource, HttpClaimFactSource>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Claims:BaseUrl"] ?? "http://claims-service:8080"));
// 19.5 — policy query, member query, coverage details, administrative 360.
builder.Services.AddScoped<AdministrativeQuery>();
builder.Services.AddScoped<IPlanVersionResolver, PlanVersionResolver>();
builder.Services.AddMemoryCache();
// Payer scope (design 38 §6). The token contract is frozen and carries no payer claim, so the restriction is
// resolved per request from admin-service — and FAILS CLOSED to "restricted to nothing", because payer scope's
// empty set means unrestricted and an outage must never widen access. See libs/authz/PayerScope.cs.
builder.Services.AddHttpClient<IPayerDirectory, HttpPayerDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080"));
// Branch scope, resolved ON DEMAND in member query rather than in middleware: policy administration is
// member-scoped (all branches), so narrowing every route here would enforce a boundary the surface lacks.
builder.Services.AddHttpClient<IBranchDirectory, HttpBranchDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080"));
// The 360 AGGREGATES: patient-service is called with the CALLER's token so it applies its own projection and
// writes its own PHI-read audit. An aggregator that calls with a service account is a way around the
// min-necessary rules of every service it aggregates.
builder.Services.AddHttpClient<IBeneficiaryAdministrativeSource, HttpBeneficiaryAdministrativeSource>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Patient:BaseUrl"] ?? "http://patient-service:8080"));
// 19.2b — the plan-change consumption rule is a SETTING, not a constant: ADR-0020 is unsigned, and reversing
// it later must not require migrating every member's accumulator.
builder.Services.Configure<MembershipOptions>(builder.Configuration.GetSection(MembershipOptions.SectionName));
// 18.A1 (X1) — consume the fulfillment streams and move coverage_limit.consumed_value. Without this the
// accumulator never advances and every member is eligible forever.
builder.Services.Configure<ConsumptionConsumerOptions>(builder.Configuration.GetSection(ConsumptionConsumerOptions.SectionName));
builder.Services.AddHostedService<BenefitConsumptionConsumer>();
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("policy-service"))
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
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "policy-service" })).AllowAnonymous();

var v1 = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("policy:write"));

// 19.2 SUPERSEDES the phase-1.2 `POST /policies` that used to live here. A policy now carries a payer_id
// rather than a free-text sponsor, and issuing one is part of the membership layer — see
// EnrollmentEndpoints.MapMembership. Keeping both would have meant two handlers on one route, and the older
// one could not satisfy the payer requirement every 19.x query and report scopes by.

// Create a coverage (+ its limits) for a beneficiary → CoverageChanged + CoverageLimitChanged.
v1.MapPost("/policies/{policyId:guid}/coverages", async (Guid policyId, CreateCoverage req, PolicyDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var policy = await db.Policies.FirstOrDefaultAsync(x => x.PolicyId == policyId, ct);
    if (policy is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
    var cat = await db.BenefitCategories.FirstOrDefaultAsync(c => c.Code == req.BenefitCategoryCode, ct);
    if (cat is null) return Results.Problem(statusCode: 400, title: $"unknown benefit category '{req.BenefitCategoryCode}'");

    var cov = new Coverage
    {
        CoverageId = Guid.NewGuid(), PolicyId = policyId, BeneficiaryId = req.BeneficiaryId,
        BenefitCategoryId = cat.BenefitCategoryId, EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
        Status = CoverageStatus.Active,
        Limits = req.Limits.Select(l => new CoverageLimit
        {
            CoverageLimitId = Guid.NewGuid(),
            LimitType = Enum.Parse<LimitType>(l.LimitType),
            LimitValue = l.LimitValue, ConsumedValue = 0m,   // accumulator starts at 0
            CurrencyCode = l.CurrencyCode ?? "EGP",
            ResetPeriod = Enum.Parse<ResetPeriod>(l.ResetPeriod ?? "None"),
            // 18.A3 (X10): anchor the reset window to this coverage's own period, so the first boundary
            // crossing is a real reset and the first run of the job never wipes in-period consumption.
            LastResetOn = LimitReset.SeedLastResetOn(
                Enum.Parse<ResetPeriod>(l.ResetPeriod ?? "None"), Enum.Parse<LimitType>(l.LimitType), req.EffectiveFrom),
        }).ToList(),
    };
    db.Coverages.Add(cov);
    await db.SaveChangesAsync(ct);

    await audit.EmitAsync(new AuditEventDraft { EntityType = "coverage", EntityId = cov.CoverageId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject, FieldClasses = ["coverage"] }, ct);
    await outbox.EnqueueAsync("CoverageChanged", "policy.events", new
    {
        tenantId = cov.TenantId,
        coverageId = cov.CoverageId, beneficiaryId = cov.BeneficiaryId, category = cat.Code,
        status = cov.Status.ToString(), policyNo = policy.PolicyNo,
        effectiveFrom = cov.EffectiveFrom, effectiveTo = cov.EffectiveTo,
        limits = cov.Limits.Select(l => new { limitType = l.LimitType.ToString(), l.LimitValue, l.ConsumedValue }),
    }, ct);
    foreach (var l in cov.Limits)
        await outbox.EnqueueAsync("CoverageLimitChanged", "policy.events", new { tenantId = cov.TenantId, coverageLimitId = l.CoverageLimitId, cov.CoverageId, l.LimitType, l.LimitValue, remaining = l.Remaining }, ct);

    return Results.Created($"/api/v1/coverages/{cov.CoverageId}", new { cov.CoverageId, remaining = cov.Limits.Select(l => new { l.LimitType, l.Remaining }) });
});

// Read coverages for a beneficiary (with remaining) — consumed by eligibility (phase 2).
v1.MapGet("/coverages", async (Guid beneficiaryId, PolicyDbContext db, CancellationToken ct) =>
{
    var covs = await db.Coverages.AsNoTracking().Include(c => c.Limits)
        .Where(c => c.BeneficiaryId == beneficiaryId && !c.IsDeleted).ToListAsync(ct);
    return Results.Ok(covs.Select(c => new
    {
        c.CoverageId, c.BenefitCategoryId, status = c.Status.ToString(), c.EffectiveFrom, c.EffectiveTo,
        limits = c.Limits.Select(l => new { l.LimitType, l.LimitValue, l.ConsumedValue, remaining = l.Remaining, resetPeriod = l.ResetPeriod.ToString() }),
    }));
}).RequireAuthorization();

// Reset job: apply any due resets → each reset audited + CoverageLimitChanged (idempotent).
v1.MapPost("/coverage-limits/reset-run", async (PolicyDbContext db, IAuditClient audit, IOutbox outbox, IBusinessCalendar calendar, CancellationToken ct) =>
{
    var today = calendar.Today();   // 18.A3 — reset boundaries are Cairo days
    var limits = await db.CoverageLimits.Where(l => l.ResetPeriod != ResetPeriod.None && l.LimitType != LimitType.Lifetime).ToListAsync(ct);
    var reset = 0;
    foreach (var l in limits)
    {
        if (LimitReset.ApplyIfDue(l, today))
        {
            reset++;
            await audit.EmitAsync(new AuditEventDraft { EntityType = "coverage_limit", EntityId = l.CoverageLimitId.ToString(), Action = AuditAction.StateChange, DecisionOutcome = "reset", FieldClasses = ["coverage"] }, ct);
            await outbox.EnqueueAsync("CoverageLimitChanged", "policy.events", new { tenantId = l.TenantId, coverageLimitId = l.CoverageLimitId, reset = true, remaining = l.Remaining }, ct);
        }
    }
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { evaluated = limits.Count, reset });
});

app.MapPlanAdministration();
app.MapMembership();
app.MapNotes();
app.MapPolicyDocuments();
app.MapUtilization();   // 19.4 — utilization for member · group · plan · policy · payer (read-only)
app.MapAdministrativeQueries();   // 19.5 — policy query + member query (payer-scoped, audited, exportable)
app.MapCoverageDetails();         // 19.5 — coverage details (version in force) + administrative 360
app.MapTimeline();   // 19.3c — the change timeline (a projection over the audit stream)   // 19.3b — classified documents on policy + member   // 19.3 — signed, timestamped, append-only notes on policy + member   // 19.2 + 19.2b — policies, plans, groups, enrolment lifecycle   // 19.1 — payers, plans, effective-dated immutable plan versions

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
