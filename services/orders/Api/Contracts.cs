using Mersal.Orders.Domain;

namespace Mersal.Orders.Api;

/// <summary>Create an investigation order for a beneficiary within an encounter (US-032). Each line references a
/// code validated against masterdata for its system.</summary>
public sealed record CreateOrderRequest(
    Guid BeneficiaryId, Guid EncounterId, OrderType OrderType, DateTimeOffset? ExpiresAt, List<CreateOrderLine> Lines);

public sealed record CreateOrderLine(CodeSystem CodeSystem, string Code, string? Description, decimal QuantityOrdered);

public sealed record CancelOrderRequest(string? Reason);

public sealed record OrderLineResponse(
    Guid OrderLineId, string CodeSystem, string Code, string? Description,
    decimal QuantityOrdered, decimal QuantityConsumed, string Status)
{
    public static OrderLineResponse From(OrderLine l) => new(
        l.OrderLineId, l.CodeSystem.ToString(), l.Code, l.Description,
        l.QuantityOrdered, l.QuantityConsumed, l.Status.ToString());
}

public sealed record OrderResponse(
    Guid OrderId, string OrderNo, Guid BeneficiaryId, Guid EncounterId, Guid OrderingProviderId,
    Guid? AuthorizationId, string OrderType, string Status, DateTimeOffset RequestedAt, DateTimeOffset? ExpiresAt,
    IReadOnlyList<OrderLineResponse> Lines)
{
    public static OrderResponse From(InvestigationOrder o) => new(
        o.OrderId, o.OrderNo, o.BeneficiaryId, o.EncounterId, o.OrderingProviderId, o.AuthorizationId,
        o.OrderType.ToString(), o.Status.ToString(), o.RequestedAt, o.ExpiresAt,
        o.Lines.Select(OrderLineResponse.From).ToList());
}
