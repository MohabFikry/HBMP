using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Data;
using Mersal.BenefitPricing;
using Mersal.Eligibility.Api;
using Mersal.Eligibility.Domain;
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
// 19.2b — which plan version's terms apply on the SERVICE DATE. policy-service owns the effective-dating
// rules and exposes the resolver; a local copy would be a second place for the two to disagree about what a
// member is entitled to.
builder.Services.AddHttpClient<IPlanVersionInForce, HttpPlanVersionInForce>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Policy:BaseUrl"] ?? "http://policy-service:8080"));
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
    TierPricingService pricing, IPlanVersionInForce planVersions, IBusinessCalendar calendar,
    EligibilityDbContext db, HttpContext http,
    IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    // 32.6 — NO CATEGORY IS A QUESTION, not a malformed request.
    //
    // This used to be a 400, and the consequence was not that callers supplied one: the reception desk
    // stopped calling this endpoint altogether and computed its own verdict in the browser from a cached
    // member status. Every property this service exists to apply — the tier, the plan version in force on the
    // service date, the waiting period, the limits, the audit event — was simply absent from the answer a
    // beneficiary was given at the desk, and nothing anywhere recorded that the check had happened.
    //
    // So the category-less question is answered, at MEMBERSHIP scope, with the answer labelled as such and no
    // cost share attached. The audit below fires either way, which is the point: a check that happened is on
    // the chain whether or not the desk knew what care was coming.
    if (string.IsNullOrWhiteSpace(req.BenefitCategory))
    {
        var membership = await checker.CheckMembershipAsync(req.BeneficiaryId, ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "eligibility", EntityId = req.BeneficiaryId.ToString(),
            Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
            DecisionOutcome = membership.Result.Decision.ToString(),
            DecisionReasonCode = "membership-scope",
            FieldClasses = ["coverage", "eligibility"],
        }, ct);

        return Results.Ok(EligibilityCheckResponse.From(
            membership.Result, membership.ExpiresAt, membership.FromCache,
            // NOT a null cost share dressed as an absent one: the desk is told WHY there is no number, so
            // "no copay shown" cannot be read as "no copay due".
            new CostSharePreviewResponse(
                null, null, IsCoveredAtTier: false, RequiresPreauthAtTier: false,
                Determinate: false,
                Reason: "No benefit category was named, so no cost share could be quoted. This is NOT a "
                      + "report that the member pays nothing.",
                null, null, null, null, false, null, null, null),
            EligibilityDecisionScope.Membership));
    }

    // 19.1b — resolve the tier FIRST, because the tier can make a service gated that is open-access
    // elsewhere (requires_preauth_override). That has to reach the decision, not just the preview: showing a
    // co-pay beside an "Eligible" verdict for care that actually needed authorization is the worst of both.
    EligibilityTierContext? tierContext = null;
    CostSharePreviewResponse? preview = null;
    var effectivelyGated = req.ServiceRequiresPreAuth ?? false;

    // 19.2b — resolve the member's plan version when the caller cannot name one.
    //
    // Until this existed the check read "no plan version, no quote", and no caller on the platform could
    // supply one, so the shared pricing path was unreachable in production.
    //
    // THE VERSION IN FORCE ON THE SERVICE DATE, not the one the member enrolled under. The first cut used the
    // coverage's `plan_version_id` — which is PROVENANCE, what the cover was projected from — and that pinned
    // every future quote to the terms in force the day they enrolled: amend the plan and nobody already on it
    // ever sees the change. The rule the effective-dated layer actually encodes, and which
    // `CoverageDetailEndpoints` already applies, is one rule that gets both cases right: February's care
    // prices at February's version, today's care at today's.
    //
    // The projected version stays as the FALLBACK, for a coverage created outside the enrolment path (no
    // plan) or when policy-service cannot be reached. A caller-supplied version still wins outright: claims
    // re-adjudicating an old service date knows better than any projection.
    var coverage = await db.Coverages.AsNoTracking()
        // ILike is belt-and-braces on top of eligibility migration 0006, which fixed the projection to hold
        // the canonical CODE and now CHECK-constrains it. The engine compares with OrdinalIgnoreCase; a
        // case-sensitive SQL match here would resolve nothing and every quote would come back indeterminate
        // for a reason that has nothing to do with the member's cover.
        .Where(c => c.BeneficiaryId == req.BeneficiaryId
                    && EF.Functions.ILike(c.BenefitCategory, req.BenefitCategory))
        .Select(c => new { c.PlanId, c.PlanVersionId })
        .FirstOrDefaultAsync(ct);

    var resolvedPlanVersionId = req.PlanVersionId;
    if (resolvedPlanVersionId is null && coverage?.PlanId is { } planId)
    {
        resolvedPlanVersionId = await planVersions.InForceAsync(
            planId, req.ServiceDate ?? calendar.Today(), http.Request.Headers.Authorization.FirstOrDefault(), ct);
    }
    resolvedPlanVersionId ??= coverage?.PlanVersionId;

    // No plan version, no quote — still. A member whose coverage carries no version is not priced at zero;
    // the verdict is returned without a cost share, and the preview says why.
    if (req.ProviderId is { } providerId && resolvedPlanVersionId is { } planVersionId)
    {
        var serviceDate = req.ServiceDate ?? calendar.Today();
        tierContext = new EligibilityTierContext(providerId, serviceDate, req.LocationId);
        var query = new TierQuery(providerId, serviceDate, req.LocationId, req.ServiceCode);
        var bearer = http.Request.Headers.Authorization.FirstOrDefault();

        var priced = await pricing.PriceAsync(
            new TierPricingRequest(planVersionId, req.BenefitCategory, query,
                Mersal.Amounts.Money.Egp(req.EstimatedAmount ?? 0m)),
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
                // Three failures, three sentences. They were two, and the collapse mattered: a policy-service
                // refusal or outage was reported as "this plan does not price this category", which is a
                // claim about the member's benefit made on the strength of an error.
                Reason: priced.Failure switch
                {
                    TierPricingFailure.TierUnresolved =>
                        "No network tier could be resolved for this provider on this service date.",
                    TierPricingFailure.Unavailable =>
                        "The plan's cost share could not be read, so the member's share is unknown. This is "
                        + "NOT a report that the service is free or uncovered.",
                    _ => "This plan version does not price this benefit category at the resolved tier.",
                },
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
})
        .Produces<EligibilityCheckResponse>();

// Lightweight member-status read for visit gating (2.3): emr-service reads this before creating a visit.
//
// AUDITED, because it is a PHI read. It was not, and "lightweight" was the reason it looked exempt: it
// returns three fields and no clinical content. But the three are a named person's membership status and
// member number, and the question this endpoint answers — "is this individual a member, and in good
// standing?" — is precisely the one a disclosure enquiry asks about. It sits on the path emr calls before
// every visit, so it is also among the most-called reads on the platform; a surface that answers about a
// person that often and records nothing is where an unexplained lookup would hide.
//
// A MISS is audited too. "Is this person one of yours?" answered no is still an answer about them, and on an
// identifier lookup it is a disclosure — an absent record cannot be the one case that leaves no trace.
v1.MapGet("/members/{beneficiaryId:guid}/status", async (
    Guid beneficiaryId, EligibilityDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me,
    CancellationToken ct) =>
{
    var m = await db.Members.AsNoTracking().FirstOrDefaultAsync(x => x.BeneficiaryId == beneficiaryId, ct);

    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = "member", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
        ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
        DecisionOutcome = m is null ? "NotFound" : "Allow",
        DecisionReasonCode = "member-status", FieldClasses = ["coverage"],
    }, ct);

    return m is null
        ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found")
        : Results.Ok(new { beneficiaryId, status = m.Status, memberNo = m.MemberNo });
});

// ================================================================ RECEPTION SEARCH (2.2, US-010)
// Reception may confirm eligibility fast WITHOUT ever seeing clinical/EMR data. The result card is a
// server-side min-necessary projection — EMR fields are absent by construction (11-permission-matrix).
// Member lookup is a front-of-house capability, and there are two front-of-house surfaces: the branch desk
// (reception:search) and the call centre (callcentre:read, its own gated "may look a member up" grant). The call
// centre's façade forwards the AGENT's token here, so requiring reception:search alone meant every call-centre
// member search was refused — and HttpMemberDirectory turned that refusal into an empty result, so the agent saw
// "No match — try a phone number or another identifier" for a member who plainly exists. Granting the call
// centre reception:search would have worked too, but it would entrench a misnomer on a role that is not
// reception; accepting either scope says what is actually true.
var reception = app.MapGroup("/api/v1/reception")
    .RequireAuthorization(HbmpPolicies.AnyScope("reception:search", "callcentre:read"));

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
})
        .Produces<ReceptionSearchResponse>();

// ---------------------------------------------------------------- VERIFIED LOOKUP (33.9)
//
// `POST /reception/verify` — the identifier the beneficiary presented, corroborated by part of their name.
//
// WHY THIS EXISTS. The eligibility screen ran `/reception/search` on one free-text box and then checked the
// FIRST hit. Typing "Ahmed" matched every Ahmed on the platform, the database's ordering chose one, and the
// plan, remaining cap and visit verdict on screen belonged to a person nobody had picked — with nothing on
// the card to say there had been others. That is a wrong-patient defect: the desk turns somebody away, or
// admits them, on another member's coverage.
//
// WHY IT IS HERE AND NOT IN THE SCREEN. A rule the browser applies is a rule for whoever is looking at that
// browser. Stated here it is the same for the SPA, the call centre and anything built next, it is audited
// once, and the refusal cannot be stepped over by a caller that skips the check and asks for the card
// directly — because there is no path from a name fragment to a card on this endpoint at all.
//
// WHAT IT IS NOT. Corroboration, not authentication. It stops the wrong RECORD being opened; it does not
// prove the person at the desk is the person on the card, and nothing downstream may treat it as though it
// did. See IdentityCorroboration.
reception.MapPost("/verify", async (
    ReceptionVerifyRequest body, IReceptionIndex index, IAuditClient audit, IHbmpPrincipalAccessor me,
    CancellationToken ct) =>
{
    var identifier = (body?.Identifier ?? "").Trim();
    var name = (body?.Name ?? "").Trim();

    if (identifier.Length == 0)
        return Results.Problem(statusCode: 400, title: "identifier is required (Card / National ID / Refugee ID / UNHCR / Passport / Policy)");
    // A refusal, not a 400: too short is a thing the operator can fix by typing more, and it must read the
    // same way as the other two refusals rather than arriving as a transport error.
    if (!IdentityCorroboration.IsUsableFragment(name))
        return Results.Ok(ReceptionVerifyResponse.Refused(ReceptionVerifyResponse.NameTooShort));

    var doc = await index.FindByPresentedIdentifierAsync(identifier, ct);
    var corroborated = doc is not null && IdentityCorroboration.NameCorroborates(doc.GivenName, doc.FamilyName, name);

    // Audited whichever way it goes. A MISS is a disclosure too — "is this identifier one of yours?" answered
    // no is still an answer about a person — and the mismatch is the one worth being able to find later: a
    // run of them across different numbers from one desk is somebody trying identifiers, and it is invisible
    // unless each attempt left a row.
    //
    // EntityId is the beneficiary where one resolved, and the IDENTIFIER OFFERED where none did. There is no
    // entity to name in that case, and recording nothing would make the failed attempts the only ones an
    // investigator could not follow.
    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = corroborated ? "member" : "reception_verify",
        EntityId = corroborated ? doc!.BeneficiaryId.ToString() : identifier,
        Action = AuditAction.Read,
        ActorUserId = me.Principal?.Subject, ActorRole = me.Principal?.Roles.FirstOrDefault(),
        TenantId = me.Principal?.TenantId,
        DecisionOutcome = corroborated ? "Allow" : "Deny",
        DecisionReasonCode = corroborated ? "verified"
            : doc is null ? ReceptionVerifyResponse.NotFound : ReceptionVerifyResponse.NameMismatch,
        FieldClasses = ["identity", "coverage"],
        // High only for the mismatch. Not-found is ordinary — a mis-read digit happens all day — but a
        // correct identifier with the wrong name is either a real mix-up at the counter or an attempt, and
        // both are things somebody should be able to go and look at.
        Severity = corroborated || doc is null ? AuditSeverity.Info : AuditSeverity.High,
    }, ct);

    if (corroborated) return Results.Ok(ReceptionVerifyResponse.Of(ReceptionResultCard.From(doc!)));

    // Both refusals return the SAME shape with nothing in it but the reason. In particular the mismatch does
    // not echo the name on file — see ReceptionVerifyResponse.
    return Results.Ok(ReceptionVerifyResponse.Refused(
        doc is null ? ReceptionVerifyResponse.NotFound : ReceptionVerifyResponse.NameMismatch));
})
        .Produces<ReceptionVerifyResponse>();

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
