namespace Mersal.Inventory.Domain;

/// <summary>
/// Two categories with genuinely different rules (design 42 §5). Medical stock is batch- and expiry-tracked
/// because a consumable whose batch nobody recorded cannot be recalled, and one whose expiry nobody recorded
/// cannot be blocked from issue. Non-medical is stationery and cleaning supplies: neither applies.
/// </summary>
public enum ItemCategory { Medical, NonMedical }

public enum ItemStatus { Active, Discontinued }

/// <summary>
/// The kinds of movement. Each carries a FIXED SIGN except the two that record a variance, and the sign is
/// stored on the row rather than derived at read time — on-hand is then a plain SUM, and a reader never has
/// to know the convention to get the right answer.
/// </summary>
public enum MovementKind
{
    /// <summary>Stock arriving from a supplier. Positive.</summary>
    Receipt,
    /// <summary>Stock consumed in the clinic. Negative. NEVER "issued to a patient" — see the header of
    /// migration 0001: clinic inventory has no patient-dispensing path at all.</summary>
    Issue,
    /// <summary>Leaving this branch for another. Negative; paired with a TransferIn.</summary>
    TransferOut,
    /// <summary>Arriving at this branch from another. Positive; paired with a TransferOut.</summary>
    TransferIn,
    /// <summary>A correction. Either direction, reason mandatory.</summary>
    Adjustment,
    /// <summary>Stock destroyed or discarded — expired, damaged, contaminated. Negative, reason mandatory.</summary>
    WriteOff,
    /// <summary>Unused stock coming back (from a ward, from a transfer that did not travel). Positive.</summary>
    Return,
    /// <summary>The VARIANCE recorded by a physical stock-take — never an overwrite of history. Either
    /// direction, reason mandatory.</summary>
    Count,
}

public sealed class Item
{
    public Guid ItemId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Sku { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public ItemCategory Category { get; set; }
    public string UnitOfMeasure { get; set; } = default!;
    public bool IsBatchTracked { get; set; }
    public bool RequiresExpiry { get; set; }

    /// <summary>D1 — pinned to false by a CHECK constraint. Enabling controlled substances is a deliberate
    /// migration, not a checkbox: a controlled register needs dual signature, a running balance per ampoule
    /// and regulator-facing reporting, which is a module of its own.</summary>
    public bool IsControlled { get; set; }

    public string? StorageCondition { get; set; }
    public bool ColdChain { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Active;

    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class BranchItem
{
    public Guid BranchId { get; set; }
    public Guid ItemId { get; set; }
    public string TenantId { get; set; } = default!;
    public decimal ReorderLevel { get; set; }
    public int LeadTimeDays { get; set; }
    public bool IsStocked { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class StockBatch
{
    public Guid BatchId { get; set; }
    public string TenantId { get; set; } = default!;
    public Guid ItemId { get; set; }
    public string BatchNo { get; set; } = default!;
    /// <summary>Null only for a non-medical item — enforced by trigger against the item's category.</summary>
    public DateOnly? ExpiryDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// One row of the append-only ledger. There is deliberately no <c>IsDeleted</c> and no <c>UpdatedAt</c>: a
/// mistake is corrected by a FURTHER movement, which is what keeps the history reconstructable. The database
/// refuses UPDATE and DELETE by trigger and by withheld grant.
/// </summary>
public sealed class StockMovement
{
    public Guid MovementId { get; set; }
    public string TenantId { get; set; } = default!;
    public Guid BranchId { get; set; }
    public Guid ItemId { get; set; }
    public Guid? BatchId { get; set; }
    public MovementKind Kind { get; set; }

    /// <summary>SIGNED by kind. On-hand is <c>SUM(quantity)</c>; there is no <c>quantity_on_hand</c> column
    /// anywhere, because a balance you can recompute is a balance you can reconcile.</summary>
    public decimal Quantity { get; set; }

    public string? Reason { get; set; }
    public Guid? TransferRef { get; set; }
    public Guid? CounterpartyBranchId { get; set; }
    public string Actor { get; set; } = default!;
    public DateTimeOffset OccurredAt { get; set; }
    public string IdempotencyKey { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }

    // NO BeneficiaryId. NO PatientId. NO EncounterId. NO PrescriptionId. Not now, not "temporarily".
    // Design 42 §7 rules 8 and 9, and NoPhiInInventoryTests asserts it over the schema and the routes.
}
