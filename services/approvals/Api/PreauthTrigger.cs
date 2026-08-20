using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.BenefitPricing;
using Microsoft.AspNetCore.Mvc;

namespace Mersal.Approvals.Api;

/// <summary>
/// Phase 19.1b consumption — is pre-authorization required for THIS care, at THIS provider, on THIS date?
///
/// Approvals leads the three consumer wirings because it is the gate that prevents the bad state. Eligibility
/// makes a promise to a beneficiary at the counter; claims lives with the consequence weeks later; this is the
/// one that stops a service being delivered ungated when the tier says it needed authorization.
///
/// It deliberately shares <see cref="TierPricingService"/> with eligibility and claims rather than resolving
/// the tier itself. An approval and the claim that follows it must not be able to disagree about which tier the
/// care was delivered at — and two resolution paths is precisely how they would come to.
/// </summary>
public static class PreauthTriggerEndpoints
{
    public static void MapPreauthTrigger(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1/authorizations")
            .RequireAuthorization(HbmpPolicies.Scope("auth:read"));

        // The service date is REQUIRED and is the date of care, never today. A provider moved into a tier that
        // demands pre-authorization in March must not retroactively make February's ungated service a breach.
        read.MapGet("/preauth-required", async (
            Guid planVersionId, string benefitCategoryCode, Guid providerId, DateOnly serviceDate,
            Guid? locationId, string? serviceCode, decimal? estimatedAmount,
            // [FromServices] is NOT decoration. `TierPricingService` is a plain class, so minimal APIs infer it
            // as a BODY parameter — illegal on a GET — and the route table throws at first request. That is why
            // this endpoint was never registered: registering it would have taken out EVERY route on the
            // service, because endpoint construction is one composite operation. It sat as dead code instead.
            [FromServices] TierPricingService pricing,
            [FromServices] RuleApplication engine,
            [FromServices] TimeProvider clock,
            IHbmpPrincipalAccessor me, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(benefitCategoryCode))
                return ProblemResults.Invalid("CATEGORY_REQUIRED", "A benefit category code is required.");

            var query = new TierQuery(providerId, serviceDate, locationId, serviceCode);
            var bearer = http.Request.Headers.Authorization.FirstOrDefault();
            var d = await pricing.RequiresPreauthAsync(planVersionId, benefitCategoryCode, query, bearer, ct);

            // ADR-0035 §5.2 — the supervisor's rules may ALSO require a decision. Only ever additive: asked
            // only when the plan says no, so there is no path by which a rule removes a contractual gate.
            PreauthRuleOutcome? byRule = null;
            if (!d.Required)
            {
                byRule = await engine.PreauthAsync(
                    benefitCategoryCode,
                    string.IsNullOrWhiteSpace(serviceCode) ? [] : [serviceCode],
                    estimatedAmount, providerId, clock.GetUtcNow(), ct);
            }

            // The fail-closed reading lives in TierPricingService; what belongs here is SAYING SO, so a caller
            // can tell "the plan does not require authorization" from "we could not tell, so we required one"
            // — and now from "the plan does not, but this tenant's supervisor does", which is a third thing
            // with a different remedy: the plan is contractual, a rule is a local decision somebody can change.
            return Results.Ok(new PreauthRequirementView(
                d.Required || byRule is not null, d.Tier?.TierCode, d.Tier?.Basis, d.Determinate,
                byRule is not null ? byRule.Reason : d.Determinate ? null : d.Failure switch
                {
                    TierPricingFailure.TierUnresolved =>
                        "No network tier could be resolved for this provider on this service date; " +
                        "authorization is required until one can be.",
                    TierPricingFailure.NotPricedAtTier =>
                        "This plan version does not price this benefit category at the resolved tier; " +
                        "authorization is required until it does.",
                    _ => "Pre-authorization could not be determined; authorization is required.",
                },
                RequiredByRule: byRule?.RuleId));
        })
        .Produces<PreauthRequirementView>();
    }
}

/// <summary><c>Determinate</c> is part of the contract, not decoration: "the plan says no authorization is
/// needed" and "we could not tell, so we are requiring one" produce different follow-up, and a caller that
/// cannot distinguish them will treat a resolution outage as a benefit decision.</summary>
/// <param name="RequiredByRule">
/// Set when a TENANT RULE required this rather than the plan. The distinction matters to whoever is stopped:
/// a contractual requirement is not something anybody local can change, and a rule is — so a provider arguing
/// about the first is wasting their time and about the second is raising a legitimate question.
/// </param>
public sealed record PreauthRequirementView(
    bool Required, string? TierCode, string? Basis, bool Determinate, string? Reason,
    Guid? RequiredByRule = null);
