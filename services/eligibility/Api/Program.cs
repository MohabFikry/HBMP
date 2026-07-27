using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Data;
using Mersal.BenefitPricing;
using Mersal.Eligibility.Api;
using Mersal.Eligibility.Infrastructure;
using Mersal.Events;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
// 19.1b — the SHARED tier-pricing path. Same composition and same libs/money split claims adjudicates with,
// so the amount quoted at the counter and the amount billed cannot diverge.
builder.Services.AddHbmpTierPricing(builder.Configuration);
builder.Services.AddHbmpAuditClient("eligibility-service");
builder.Services.AddHbmpAuthorization();
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<EligibilityDbContext>();
builder.Services.AddHbmpOutboxRelay();   // relay staged audit events to RabbitMQ
builder.Services.AddEligibilityInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<ConsumerOptions>(builder.Configuration.GetSection(ConsumerOptions.SectionName));
builder.Services.AddSingleton<ConsumerHealthState>();
builder.Services.AddHostedService<EventConsumer>();
builder.Services.AddHealthChecks()
    .AddCheck<EventConsumerHealthCheck>("event-consumer", tags: ["ready"]);

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("eligibility-service"))
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

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "eligibility-service" })).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

// Coordination coverage summary (10.1) — the fail-closed spine of the case-service beneficiary-360 view.
app.MapCoordination();

var v1 = app.MapGroup("/api/v1/eligibility").RequireAuthorization(HbmpPolicies.Scope("eligibility:check"));

// POST /eligibility/check — cache-first decision; every check is an audited PHI read.
v1.MapPost("/check", async (
    EligibilityCheckRequest req, EligibilityChecker checker, IAuditClient audit,
    TierPricingService pricing, IBusinessCalendar calendar, HttpContext http,
    IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.BenefitCategory))
        return Results.Problem(statusCode: 400, title: "benefitCategory is required");

    // 19.1b — resolve the tier FIRST, because the tier can make a service gated that is open-access
    // elsewhere (requires_preauth_override). That has to reach the decision, not just the preview: showing a
    // co-pay beside an "Eligible" verdict for care that actually needed authorization is the worst of both.
    EligibilityTierContext? tierContext = null;
    CostSharePreviewResponse? preview = null;
    var effectivelyGated = req.ServiceRequiresPreAuth ?? false;

    // No plan version, no quote. The member→plan link lands in 19.2b; until then a caller that cannot
    // name the version gets the verdict without a cost share rather than a number derived from nothing.
    if (req.ProviderId is { } providerId && req.PlanVersionId is { } planVersionId)
    {
        var serviceDate = req.ServiceDate ?? calendar.Today();
        tierContext = new EligibilityTierContext(providerId, serviceDate, req.LocationId);
        var query = new TierQuery(providerId, serviceDate, req.LocationId, req.ServiceCode);
        var bearer = http.Request.Headers.Authorization.FirstOrDefault();

        var priced = await pricing.PriceAsync(
            new TierPricingRequest(planVersionId, req.BenefitCategory, query,
                Mersal.Money.Money.Egp(req.EstimatedAmount ?? 0m)),
            bearer, ct);

        if (priced.Pricing is { } quote)
        {
            effectivelyGated = effectivelyGated || quote.RequiresPreauth;
            var hasAmount = req.EstimatedAmount is > 0m;
            preview = new CostSharePreviewResponse(
                quote.Tier.TierCode, quote.Tier.Basis, quote.Terms.IsCovered, quote.RequiresPreauth,
                Determinate: true, Reason: null,
                EstimatedAllowedAmount: hasAmount ? quote.Split.AllowedAmount.Amount : null,
                EstimatedMemberShare: hasAmount ? quote.Split.MemberShare.Amount : null,
                EstimatedPayerShare: hasAmount ? quote.Split.PayerShare.Amount : null,
                quote.Terms.Deductible, quote.Terms.DeductibleWaived,
                quote.Terms.CopayFixed, quote.Terms.CopayPercent, quote.Terms.CoinsurancePercent);
        }
        else
        {
            // FAIL CLOSED, and SAY SO. A zero here would read as "free" to the person being told. Gating is
            // assumed until it can be resolved, for the same reason approvals does.
            effectivelyGated = true;
            preview = new CostSharePreviewResponse(
                null, null, IsCoveredAtTier: false, RequiresPreauthAtTier: true,
                Determinate: false,
                Reason: priced.Failure == TierPricingFailure.TierUnresolved
                    ? "No network tier could be resolved for this provider on this service date."
                    : "This plan version does not price this benefit category at the resolved tier.",
                null, null, null, null, false, null, null, null);
        }
    }

    var outcome = await checker.CheckAsync(
        req.BeneficiaryId, req.BenefitCategory, req.ServiceCode, effectivelyGated, tierContext, ct);

    // Eligibility checks read PHI (member status + coverage) → always audited.
    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = "eligibility", EntityId = req.BeneficiaryId.ToString(),
        Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
        DecisionOutcome = outcome.Result.Decision.ToString(),
        FieldClasses = ["coverage", "eligibility"],
    }, ct);

    return Results.Ok(EligibilityCheckResponse.From(outcome.Result, outcome.ExpiresAt, outcome.FromCache, preview));
});

// Lightweight member-status read for visit gating (2.3): emr-service reads this before creating a visit.
v1.MapGet("/members/{beneficiaryId:guid}/status", async (
    Guid beneficiaryId, EligibilityDbContext db, CancellationToken ct) =>
{
    var m = await db.Members.AsNoTracking().FirstOrDefaultAsync(x => x.BeneficiaryId == beneficiaryId, ct);
    return m is null
        ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found")
        : Results.Ok(new { beneficiaryId, status = m.Status, memberNo = m.MemberNo });
});

// ================================================================ RECEPTION SEARCH (2.2, US-010)
// Reception may confirm eligibility fast WITHOUT ever seeing clinical/EMR data. The result card is a
// server-side min-necessary projection — EMR fields are absent by construction (11-permission-matrix).
var reception = app.MapGroup("/api/v1/reception").RequireAuthorization(HbmpPolicies.Scope("reception:search"));

reception.MapGet("/search", async (
    string? q, IReceptionIndex index, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.Problem(statusCode: 400, title: "q is required (NationalID / Passport / Card / Policy / Phone / name)");

    var hits = await index.SearchAsync(q, limit: 25, ct);
    var cards = hits.Select(ReceptionResultCard.From).ToList();

    // Every reception search is an audited PHI read.
    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = "reception_search", EntityId = q,
        Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
        DecisionOutcome = $"{cards.Count} match(es)", FieldClasses = ["identity", "coverage"],
    }, ct);

    var hint = cards.Count == 0 ? "No match — try another identifier (Passport / Card / Policy / Phone) or register the beneficiary." : null;
    return Results.Ok(new ReceptionSearchResponse(q, cards.Count, cards, hint));
});

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
