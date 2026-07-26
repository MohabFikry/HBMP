namespace Mersal.Provider.Domain;

// Phase 14.1 — the internal Mersal branch (37 §2). provider-service's remit widens from "contracted
// network" to "network & facilities": external providers (Provider/ProviderLocation) AND Mersal-operated
// branches, kept as SEPARATE tables. A branch is an INTERNAL org unit — it is NOT a provider_location
// (a contracted third-party site). Only branches are subject to staff branch-scoping (14.2+). Branch is
// slow-changing org reference data with no PHI, so it is readable by any authenticated user; writes are
// restricted to the Network Team / Org Admin and audited. Branch codes are stable business keys used in
// downstream business keys and reporting.

/// <summary>Branch lifecycle (37 §2.1). Only <see cref="Active"/> branches host operations; Suspended/Closed
/// stay readable for history and reporting (soft-delete + audit, never hard-deleted).</summary>
public enum BranchStatus { Active, Suspended, Closed }

public sealed class Branch
{
    public Guid BranchId { get; set; }
    public string BranchCode { get; set; } = default!;   // ASW | ALX | OCT | MAA | DOK | NSR — stable UK
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public string? City { get; set; }
    public string? Address { get; set; }
    public string Timezone { get; set; } = "Africa/Cairo";
    public string? Phone { get; set; }
    /// <summary>Opening hours as a JSON document (jsonb). Free-form here; the UI owns its shape.</summary>
    public string? OpeningHours { get; set; }
    public BranchStatus Status { get; set; } = BranchStatus.Active;
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
