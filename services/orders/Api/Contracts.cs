using Mersal.Orders.Domain;

namespace Mersal.Orders.Api;

/// <summary>Create an investigation order for a beneficiary within an encounter (US-032). Each line references a
/// code validated against masterdata for its system.</summary>
/// <param name="ProcedureTypeCode">
/// 31.1 — the OP-Procedure KIND, at the ORDER level, because it is ONE clinical decision (design 45 §2,
/// revised). Optional so every existing Lab/Radiology caller compiles unchanged; a Procedure order without
/// one is refused 422 the same way a line without one used to be.
/// </param>
/// <param name="Sessions">
/// 31.1 — the course length in attendances. NULL when the type is not delivered in sessions, which is a
/// different fact from 1 and must not be sent as one.
/// </param>
public sealed record CreateOrderRequest(
    Guid BeneficiaryId, Guid EncounterId, OrderType OrderType, DateTimeOffset? ExpiresAt, List<CreateOrderLine> Lines,
    string? ProcedureTypeCode = null,
    int? Sessions = null);

/// <summary>A line on a new order.</summary>
/// <param name="QuantityOrdered">
/// For a Lab or Radiology line, the quantity. For an OP-Procedure line it is superseded by
/// <paramref name="QuantityPerSession"/> when that is supplied, and kept only so pre-31.1 callers still work.
/// </param>
/// <param name="QuantityPerSession">
/// 31.1 — how much of THIS item is delivered at each attendance. The stored <c>quantity_ordered</c> — what
/// consume meters and approvals narrow — becomes <c>sessions x this</c>.
/// </param>
/// <param name="ProcedureTypeCode">
/// DEPRECATED at the line level by 31.1: the kind belongs to the ORDER. Still accepted and still validated,
/// so a pre-31.1 caller is neither broken nor silently ignored — an ignored type field is decorative, and
/// every report built on it would be quietly wrong.
/// </param>
public sealed record CreateOrderLine(
    CodeSystem CodeSystem, string Code, string? Description, decimal QuantityOrdered,
    Guid? ExaminationTypeId = null, string? ProcedureTypeCode = null,
    decimal? QuantityPerSession = null);

public sealed record CancelOrderRequest(string? Reason);

// --- 14.7 sensitive-result release workflow ---------------------------------------------------
public sealed record RaiseAccessRequest(Guid OrderId, Guid OrderLineId, string PurposeCode, string Justification, int RequestedTtlHours);

public sealed record AccessDecision(string Decision, int? TtlHours, string? Reason);

/// <summary>18.A4 — the requester answering an InfoRequested decision. The supplement is APPENDED to the
/// original justification; nothing is overwritten (23 §11).</summary>
public sealed record SupplyInfo(string Supplement);

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
    DateTimeOffset? ExpiresAt,
    /// <summary>
    /// Past its validity window, computed against the clock rather than read from <c>Status</c>.
    ///
    /// <para>The expiry sweeper runs hourly, so between lapsing and being swept the row still says Active.
    /// A queue that trusted the status would offer a technician an order the consume rule refuses. The rule
    /// compares the date; this makes the SCREEN agree with it.</para>
    /// </summary>
    bool Expired,
    IReadOnlyList<QueueLineResponse> Lines)
{
    /// <summary>Projects only the still-available lines (Active/PartiallyUsed) of an order the caller may fulfil.</summary>
    public static QueueItemResponse From(InvestigationOrder o, DateTimeOffset now) => new(
        o.OrderId, o.OrderNo, o.OrderType.ToString(), o.BeneficiaryId, o.Status.ToString(), o.RequestedAt,
        o.ExpiresAt,
        o.Status == OrderStatus.Expired || (o.ExpiresAt is { } exp && exp <= now),
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
