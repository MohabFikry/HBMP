namespace Mersal.Provider.Domain;

// Provider domain per 15-database-erd §provider + 22-data-dictionary §5. Every row is tenant-scoped
// (tenant_id) and — for provider-owned rows — provider-scoped; isolation is enforced in depth
// (ABAC provider-ownership + PostgreSQL RLS) — see phase 2b THE INVARIANT. Soft-delete + history only;
// Suspended/Terminated providers stay readable for audit, never hard-deleted.

/// <summary>Canonical persisted provider type (22 §5.1). Business labels (e.g. Doctor, ImagingCenter)
/// are a presentation mapping over this enum — see <see cref="ProviderTypeLabels"/>.</summary>
public enum ProviderType { Hospital, Clinic, Lab, Pharmacy, Imaging }

/// <summary>Canonical provider lifecycle status (22 §5.1). Only <see cref="Active"/> is routable.</summary>
public enum ProviderStatus { Active, Suspended, Terminated }

/// <summary>Explicit, auditable onboarding state machine (phase 2b.2). Draft→…→Activated, then the
/// post-activation states Suspended/Terminated mirror <see cref="ProviderStatus"/>.</summary>
public enum OnboardingState { Draft, DocumentsCollected, Credentialed, Contracted, Activated, Suspended, Terminated }

public enum ContractStatus { Draft, Active, Expired, Terminated }

public enum ServiceType { Lab, Imaging, Consult, Procedure }

public enum CodeSystem { CPT, LOINC, LOCAL }

public enum CredentialStatus { Pending, Valid, Expired, Rejected }

public sealed class Provider
{
    public Guid ProviderId { get; set; }
    public string TenantId { get; set; } = default!;
    public string ProviderCode { get; set; } = default!;   // MRS-PRV-* business key
    public string LegalName { get; set; } = default!;
    public ProviderType ProviderType { get; set; }
    public ProviderStatus Status { get; set; } = ProviderStatus.Suspended;   // not routable until Activated
    public OnboardingState OnboardingState { get; set; } = OnboardingState.Draft;
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProviderLocation> Locations { get; set; } = [];
    public List<ProviderContract> Contracts { get; set; } = [];
    public List<ProviderCredential> Credentials { get; set; } = [];
}

public sealed class ProviderLocation
{
    public Guid LocationId { get; set; }
    public Guid ProviderId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Governorate { get; set; }
    public string? Address { get; set; }
    // 22 §5.2 specifies geography(Point); the dev Postgres image has no PostGIS, so lat/long are stored
    // as plain numerics (documented deviation in the migration). Semantics are preserved.
    public decimal? GeoLat { get; set; }
    public decimal? GeoLng { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class ProviderContract
{
    public Guid ContractId { get; set; }
    public Guid ProviderId { get; set; }
    public string TenantId { get; set; } = default!;
    public string ContractNo { get; set; } = default!;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Draft;
    public bool IsDeleted { get; set; }
    public List<ContractServiceLine> ServiceLines { get; set; } = [];
}

public sealed class ContractServiceLine
{
    public Guid ServiceLineId { get; set; }
    public Guid ContractId { get; set; }
    public string TenantId { get; set; } = default!;
    public ServiceType ServiceType { get; set; }
    public CodeSystem CodeSystem { get; set; }
    public string Code { get; set; } = default!;
    /// <summary>T2 financial — masked from callers without provider-financial permission (18-security §8).</summary>
    public decimal AgreedPrice { get; set; }
    public string CurrencyCode { get; set; } = "EGP";
}

public sealed class ProviderCredential
{
    public Guid CredentialId { get; set; }
    public Guid ProviderId { get; set; }
    public string TenantId { get; set; } = default!;
    public string CredentialType { get; set; } = default!;   // license | tax_card | accreditation | …
    public CredentialStatus Status { get; set; } = CredentialStatus.Pending;
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public Guid? DocumentId { get; set; }                    // document-service blob (CMK, malware-scanned)
    public bool IsMandatory { get; set; }                    // gates activation (FR-NET-004)
    public bool IsDeleted { get; set; }
}
