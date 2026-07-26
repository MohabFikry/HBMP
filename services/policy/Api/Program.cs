using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using PolicyEntity = Mersal.Policy.Domain.Policy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("policy-service");
builder.Services.AddHbmpAuthorization();
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<PolicyDbContext>();
builder.Services.AddHbmpOutboxRelay();   // relay staged events (incl. audit) to RabbitMQ
builder.Services.AddPolicyInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
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

// Create a policy → PolicyChanged.
v1.MapPost("/policies", async (CreatePolicy req, PolicyDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    var p = new PolicyEntity
    {
        PolicyId = Guid.NewGuid(), PolicyNo = req.PolicyNo, Sponsor = req.Sponsor,
        EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
        Status = PolicyStatus.Active, CreatedAt = now, UpdatedAt = now,
    };
    db.Policies.Add(p);
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "policy", EntityId = p.PolicyId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject }, ct);
    await outbox.EnqueueAsync("PolicyChanged", "policy.events", new { policyId = p.PolicyId, p.PolicyNo, status = p.Status.ToString() }, ct);
    return Results.Created($"/api/v1/policies/{p.PolicyId}", new { p.PolicyId, p.PolicyNo });
});

// Create a coverage (+ its limits) for a beneficiary → CoverageChanged + CoverageLimitChanged.
v1.MapPost("/policies/{policyId:guid}/coverages", async (Guid policyId, CreateCoverage req, PolicyDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var policy = await db.Policies.FirstOrDefaultAsync(x => x.PolicyId == policyId, ct);
    if (policy is null) return Results.NotFound(new { policyId });
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
        coverageId = cov.CoverageId, beneficiaryId = cov.BeneficiaryId, category = cat.Code,
        status = cov.Status.ToString(), policyNo = policy.PolicyNo,
        effectiveFrom = cov.EffectiveFrom, effectiveTo = cov.EffectiveTo,
        limits = cov.Limits.Select(l => new { limitType = l.LimitType.ToString(), l.LimitValue, l.ConsumedValue }),
    }, ct);
    foreach (var l in cov.Limits)
        await outbox.EnqueueAsync("CoverageLimitChanged", "policy.events", new { coverageLimitId = l.CoverageLimitId, cov.CoverageId, l.LimitType, l.LimitValue, remaining = l.Remaining }, ct);

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
v1.MapPost("/coverage-limits/reset-run", async (PolicyDbContext db, IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
{
    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    var limits = await db.CoverageLimits.Where(l => l.ResetPeriod != ResetPeriod.None && l.LimitType != LimitType.Lifetime).ToListAsync(ct);
    var reset = 0;
    foreach (var l in limits)
    {
        if (LimitReset.ApplyIfDue(l, today))
        {
            reset++;
            await audit.EmitAsync(new AuditEventDraft { EntityType = "coverage_limit", EntityId = l.CoverageLimitId.ToString(), Action = AuditAction.StateChange, DecisionOutcome = "reset", FieldClasses = ["coverage"] }, ct);
            await outbox.EnqueueAsync("CoverageLimitChanged", "policy.events", new { coverageLimitId = l.CoverageLimitId, reset = true, remaining = l.Remaining }, ct);
        }
    }
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { evaluated = limits.Count, reset });
});

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
