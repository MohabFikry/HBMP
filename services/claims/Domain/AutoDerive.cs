namespace Mersal.Claims.Domain;

/// <summary>A min-necessary, clinical-free description of a delivered/dispensed line handed to the auto-derive path.
/// Built at the boundary from an <c>OrderLinesConsumed</c> / <c>RxLinesDispensed</c> event — it carries ONLY billing
/// codes, quantities, the fulfillment reference, and linkage ids. Any clinical field on the source event is dropped
/// here, never reaching the claims schema.</summary>
public sealed record ClaimIntakeEvent(
    Guid EventId,
    string EventType,
    string TenantId,
    Guid FulfillmentRef,
    FulfillmentType FulfillmentType,
    Guid BeneficiaryId,
    Guid ProviderId,
    Guid? ProviderLocationId,
    Guid? AuthorizationId,
    ClaimCodeSystem CodeSystem,
    string Code,
    string? Description,
    decimal Quantity,
    decimal BilledAmount,
    DateOnly ServiceDate,
    string CurrencyCode,
    DateTimeOffset OccurredAt);

/// <summary>Pure pricing outcome for one auto-derived line — decided from the resolved contract tariff alone
/// (the full 9-step adjudication runs later in 10b.3). No tariff ⇒ <see cref="ReasonCodes.NoTariff"/> +
/// <see cref="SystemRecommendation.RequiresManualReview"/>, and the price stays null — NEVER guessed.</summary>
public static class AutoDerivePricing
{
    public const string RuleVersion = "10b.1";

    public static (decimal? ContractPrice, SystemRecommendation? Recommendation, IReadOnlyList<string> ReasonCodes)
        Price(decimal? resolvedTariff)
    {
        if (resolvedTariff is null)
            return (null, SystemRecommendation.RequiresManualReview, [ReasonCodes.NoTariff]);
        // Priced cleanly: leave the recommendation for adjudication (10b.3) to compute against the full rule set.
        return (resolvedTariff, null, []);
    }
}
