using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Eligibility.Api;
using Mersal.Eligibility.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("eligibility-service");
builder.Services.AddHbmpAuthorization();
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<EligibilityDbContext>();
builder.Services.AddHbmpOutboxRelay();   // relay staged audit events to RabbitMQ
builder.Services.AddEligibilityInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<ConsumerOptions>(builder.Configuration.GetSection(ConsumerOptions.SectionName));
builder.Services.AddHostedService<EventConsumer>();

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

// Coordination coverage summary (10.1) — the fail-closed spine of the case-service beneficiary-360 view.
app.MapCoordination();

var v1 = app.MapGroup("/api/v1/eligibility").RequireAuthorization(HbmpPolicies.Scope("eligibility:check"));

// POST /eligibility/check — cache-first decision; every check is an audited PHI read.
v1.MapPost("/check", async (
    EligibilityCheckRequest req, EligibilityChecker checker, IAuditClient audit,
    IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.BenefitCategory))
        return Results.Problem(statusCode: 400, title: "benefitCategory is required");

    var outcome = await checker.CheckAsync(
        req.BeneficiaryId, req.BenefitCategory, req.ServiceCode, req.ServiceRequiresPreAuth ?? false, ct);

    // Eligibility checks read PHI (member status + coverage) → always audited.
    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = "eligibility", EntityId = req.BeneficiaryId.ToString(),
        Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
        DecisionOutcome = outcome.Result.Decision.ToString(),
        FieldClasses = ["coverage", "eligibility"],
    }, ct);

    return Results.Ok(EligibilityCheckResponse.From(outcome.Result, outcome.ExpiresAt, outcome.FromCache));
});

// Lightweight member-status read for visit gating (2.3): emr-service reads this before creating a visit.
v1.MapGet("/members/{beneficiaryId:guid}/status", async (
    Guid beneficiaryId, EligibilityDbContext db, CancellationToken ct) =>
{
    var m = await db.Members.AsNoTracking().FirstOrDefaultAsync(x => x.BeneficiaryId == beneficiaryId, ct);
    return m is null
        ? Results.NotFound(new { beneficiaryId })
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
