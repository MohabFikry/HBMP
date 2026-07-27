using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.BenefitPricing;

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
            Guid? locationId, string? serviceCode,
            TierPricingService pricing, IHbmpPrincipalAccessor me, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(benefitCategoryCode))
                return ProblemResults.Invalid("CATEGORY_REQUIRED", "A benefit category code is required.");

            var query = new TierQuery(providerId, serviceDate, locationId, serviceCode);
            var bearer = http.Request.Headers.Authorization.FirstOrDefault();
            var d = await pricing.RequiresPreauthAsync(planVersionId, benefitCategoryCode, query, bearer, ct);

            // The fail-closed reading lives in TierPricingService; what belongs here is SAYING SO, so a caller
            // can tell "the plan does not require authorization" from "we could not tell, so we required one".
            return Results.Ok(new PreauthRequirementView(
                d.Required, d.Tier?.TierCode, d.Tier?.Basis, d.Determinate,
                d.Determinate ? null : d.Failure switch
                {
                    TierPricingFailure.TierUnresolved =>
                        "No network tier could be resolved for this provider on this service date; " +
                        "authorization is required until one can be.",
                    TierPricingFailure.NotPricedAtTier =>
                        "This plan version does not price this benefit category at the resolved tier; " +
                        "authorization is required until it does.",
                    _ => "Pre-authorization could not be determined; authorization is required.",
                }));
        });
    }
}

/// <summary><c>Determinate</c> is part of the contract, not decoration: "the plan says no authorization is
/// needed" and "we could not tell, so we are requiring one" produce different follow-up, and a caller that
/// cannot distinguish them will treat a resolution outage as a benefit decision.</summary>
public sealed record PreauthRequirementView(
    bool Required, string? TierCode, string? Basis, bool Determinate, string? Reason);
