namespace Mersal.Orders.Domain;

// Investigation-orders domain (22-data-dictionary §7, 23-state-machines §2). Canonical enums used EXACTLY.

public enum OrderType { Lab, Imaging, Procedure }
public enum CodeSystem { CPT, LOINC, LOCAL }

/// <summary>Order lifecycle (§2): Requested → PendingApproval → (Approved|Rejected) → Active → PartiallyUsed →
/// Completed; plus Expired, Cancelled. Phase 4.2 covers create + routing (up to Active/PendingApproval); the
/// consume path (Active→PartiallyUsed→Completed) is phase 5.</summary>
public enum OrderStatus { Requested, PendingApproval, Approved, Rejected, Active, PartiallyUsed, Completed, Expired, Cancelled }

public enum OrderLineStatus { Active, PartiallyUsed, Completed, Cancelled }

/// <summary>Clinical sensitivity ladder (phase 14.6, design 37 §5). Pinned from the examination type at order
/// creation so later reclassification cannot retroactively unlock already-restricted data.</summary>
public enum SensitivityLevel { Standard, Sensitive, HighlySensitive }

public sealed class InvestigationOrder
{
    public Guid OrderId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public string OrderNo { get; set; } = default!;      // ORD-YYYY-NNNNNN
    public Guid BeneficiaryId { get; set; }
    public Guid EncounterId { get; set; }
    public Guid OrderingProviderId { get; set; }
    /// <summary>The Mersal branch where the order was raised (phase 14.4). NULL for legacy/external contexts —
    /// branch scoping applies only to branch-bound orders (design 37 §3).</summary>
    public Guid? OrderingBranchId { get; set; }
    public Guid? AuthorizationId { get; set; }
    public OrderType OrderType { get; set; }
    /// <summary>Pinned sensitivity for the order = max of its lines (phase 14.6). Denormalized so read-time
    /// gating (14.7) never needs a cross-service join. Pre-existing rows default to Standard.</summary>
    public SensitivityLevel SensitivityLevel { get; set; } = SensitivityLevel.Standard;
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
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid OrderId { get; set; }
    public CodeSystem CodeSystem { get; set; }
    public string Code { get; set; } = default!;
    public string? Description { get; set; }
    /// <summary>The classified examination type this line represents (phase 14.6). NULL for legacy lines.</summary>
    public Guid? ExaminationTypeId { get; set; }
    /// <summary>Pinned sensitivity from the examination type (phase 14.6). Default Standard; results inherit it.</summary>
    public SensitivityLevel SensitivityLevel { get; set; } = SensitivityLevel.Standard;
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityConsumed { get; set; }        // accumulator, 0 ≤ consumed ≤ ordered (phase 5)
    public OrderLineStatus Status { get; set; } = OrderLineStatus.Active;
    public uint RowVersion { get; set; }                 // xmin — optimistic-concurrency guard on consume (phase 5)

    public decimal QuantityRemaining => QuantityOrdered - QuantityConsumed;
}

/// <summary>Append-only consume record (22-data-dictionary §7.3). One immutable row per consumed line: it is the
/// duplicate-proof anchor — <see cref="IdempotencyKey"/> is UNIQUE so a replayed key is rejected by the DB, and the
/// row can carry a result blob ref (phase 5.3). Never updated (except the one-time result attachment) or deleted;
/// full history lives in audit_event.</summary>
public sealed class OrderFulfillment
{
    public Guid FulfillmentId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid OrderLineId { get; set; }
    public Guid PerformingProviderId { get; set; }
    public decimal Quantity { get; set; }
    public string IdempotencyKey { get; set; } = default!;   // UNIQUE — dedup guarantee
    /// <summary>18.A3 — SHA-256 of the canonical request this row came from. A replay of the same key
    /// with a DIFFERENT payload is rejected instead of being answered with someone else's work. NULL on
    /// rows written before the column existed (treated as unverifiable, replay allowed).</summary>
    public string? RequestHash { get; set; }
    public Guid? ResultDocumentId { get; set; }              // phase 5.3 result blob ref
    public string? ResultValue { get; set; }                 // phase 5.3 structured result summary
    public DateTimeOffset? ResultUploadedAt { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
    public Guid ConsumedBy { get; set; }
}

/// <summary>Business-key formatter for orders (0A §3): <c>ORD-YYYY-NNNNNN</c>.</summary>
public static class OrderNo
{
    public static string Format(int year, int sequence) => $"ORD-{year:D4}-{sequence:D6}";
}
