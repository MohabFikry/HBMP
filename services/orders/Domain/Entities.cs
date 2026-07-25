namespace Mersal.Orders.Domain;

// Investigation-orders domain (22-data-dictionary §7, 23-state-machines §2). Canonical enums used EXACTLY.

public enum OrderType { Lab, Imaging, Procedure }
public enum CodeSystem { CPT, LOINC, LOCAL }

/// <summary>Order lifecycle (§2): Requested → PendingApproval → (Approved|Rejected) → Active → PartiallyUsed →
/// Completed; plus Expired, Cancelled. Phase 4.2 covers create + routing (up to Active/PendingApproval); the
/// consume path (Active→PartiallyUsed→Completed) is phase 5.</summary>
public enum OrderStatus { Requested, PendingApproval, Approved, Rejected, Active, PartiallyUsed, Completed, Expired, Cancelled }

public enum OrderLineStatus { Active, PartiallyUsed, Completed, Cancelled }

public sealed class InvestigationOrder
{
    public Guid OrderId { get; set; }
    public string OrderNo { get; set; } = default!;      // ORD-YYYY-NNNNNN
    public Guid BeneficiaryId { get; set; }
    public Guid EncounterId { get; set; }
    public Guid OrderingProviderId { get; set; }
    public Guid? AuthorizationId { get; set; }
    public OrderType OrderType { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Requested;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
    public uint RowVersion { get; set; }                 // xmin optimistic-concurrency token
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public Guid OrderLineId { get; set; }
    public Guid OrderId { get; set; }
    public CodeSystem CodeSystem { get; set; }
    public string Code { get; set; } = default!;
    public string? Description { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityConsumed { get; set; }        // accumulator, 0 ≤ consumed ≤ ordered (phase 5)
    public OrderLineStatus Status { get; set; } = OrderLineStatus.Active;
}

/// <summary>Business-key formatter for orders (0A §3): <c>ORD-YYYY-NNNNNN</c>.</summary>
public static class OrderNo
{
    public static string Format(int year, int sequence) => $"ORD-{year:D4}-{sequence:D6}";
}
