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

// ---- Phase 5.1: provider queue / search (min-necessary projection) ----

/// <summary>A queue/search row for a fulfilling provider. Deliberately narrow: patient by id, the order-line
/// codes/description and the quantity still available to fulfil — never diagnoses, notes, or any pharmacy data.</summary>
public sealed record QueueLineResponse(Guid OrderLineId, string CodeSystem, string Code, string? Description, decimal QuantityRemaining);

public sealed record QueueItemResponse(
    Guid OrderId, string OrderNo, string OrderType, Guid BeneficiaryId, string Status, DateTimeOffset RequestedAt,
    IReadOnlyList<QueueLineResponse> Lines)
{
    /// <summary>Projects only the still-available lines (Active/PartiallyUsed) of an order the caller may fulfil.</summary>
    public static QueueItemResponse From(InvestigationOrder o) => new(
        o.OrderId, o.OrderNo, o.OrderType.ToString(), o.BeneficiaryId, o.Status.ToString(), o.RequestedAt,
        o.Lines.Where(l => l.Status is OrderLineStatus.Active or OrderLineStatus.PartiallyUsed)
               .Select(l => new QueueLineResponse(l.OrderLineId, l.CodeSystem.ToString(), l.Code, l.Description, l.QuantityRemaining))
               .ToList());
}

// ---- Phase 5.2: consume ----

public sealed record ConsumeRequest(List<ConsumeLine> Lines);
public sealed record ConsumeLine(Guid OrderLineId, decimal Quantity);

public sealed record FulfillmentResponse(Guid FulfillmentId, Guid OrderLineId, decimal Quantity, DateTimeOffset ConsumedAt)
{
    public static FulfillmentResponse From(OrderFulfillment f) => new(f.FulfillmentId, f.OrderLineId, f.Quantity, f.ConsumedAt);
}

public sealed record ConsumeResponse(
    Guid OrderId, string OrderStatus, IReadOnlyList<OrderLineResponse> Lines, IReadOnlyList<FulfillmentResponse> Fulfillments, bool Replayed)
{
    public static ConsumeResponse From(InvestigationOrder o, IReadOnlyList<OrderFulfillment> fulfillments, bool replayed) => new(
        o.OrderId, o.Status.ToString(), o.Lines.Select(OrderLineResponse.From).ToList(),
        fulfillments.Select(FulfillmentResponse.From).ToList(), replayed);
}

// ---- Phase 5.3: result upload ----

public sealed record ResultResponse(
    Guid FulfillmentId, Guid OrderLineId, string? ResultValue, Guid? ResultDocumentId, DateTimeOffset? ResultUploadedAt)
{
    public static ResultResponse From(OrderFulfillment f) => new(f.FulfillmentId, f.OrderLineId, f.ResultValue, f.ResultDocumentId, f.ResultUploadedAt);
}
