using Mersal.Claims.Domain;

namespace Mersal.Claims.Infrastructure;

/// <summary>A delivered/authorized fulfillment that a submitted line matched to (10b.5, §5 check 4). Identifies the
/// orders.order_fulfillment / pharmacy.dispense_event record so the payable line is anchored to it — which the
/// no-double-billing index then guarantees is unique.</summary>
public sealed record FulfillmentMatch(Guid FulfillmentRef, FulfillmentType FulfillmentType, DateOnly ServiceDate);

/// <summary>Resolves whether a delivered/authorized fulfillment exists for a provider-submitted line, matching on
/// <c>(provider, beneficiary, code, service date ± tolerance, authorization)</c> (36 §3.2). This is the seam to
/// orders/pharmacy; the resolver applies the date-tolerance rule (<see cref="SubmissionMatcher"/>). Null ⇒ no
/// fulfillment record ⇒ the line is flagged NO_FULFILLMENT_RECORD and routed to manual assessment (never auto-approved).</summary>
public interface IFulfillmentResolver
{
    Task<FulfillmentMatch?> ResolveAsync(
        MatchKey key, DateOnly serviceDate, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Default resolver: resolves NOTHING, so every submitted line lands in the manual-assessment queue with
/// NO_FULFILLMENT_RECORD until the orders/pharmacy fulfillment-query wiring is live (same deferral pattern as the
/// auto-derive event consumers). Never invents a fulfillment — a false match would record the wrong payable line.</summary>
public sealed class NoFulfillmentResolver : IFulfillmentResolver
{
    public Task<FulfillmentMatch?> ResolveAsync(
        MatchKey key, DateOnly serviceDate, string? bearerToken, CancellationToken ct = default) =>
        Task.FromResult<FulfillmentMatch?>(null);
}
