namespace Mersal.Provider.Domain;

// Provider domain per 15-database-erd §provider + 22-data-dictionary §5. Every row is tenant-scoped
// (tenant_id) and — for provider-owned rows — provider-scoped; isolation is enforced in depth
// (ABAC provider-ownership + PostgreSQL RLS) — see phase 2b THE INVARIANT. Soft-delete + history only;
// Suspended/Terminated providers stay readable for audit, never hard-deleted.

/// <summary>Canonical persisted provider type (22 §5.1). Business labels (e.g. Doctor, ImagingCenter)
/// are a presentation mapping over this enum — see <see cref="ProviderTypeLabels"/>.</summary>
/// <remarks>
/// <para><b><c>Radiology</c> is the successor spelling of <c>Imaging</c>, and both are members on purpose.</b>
/// Design 45 §1 runs this as expand → backfill → contract: migration 0011 widened the CHECK to accept both,
/// 0012 rewrote every existing row to <c>Radiology</c>, and the deferred 0013 narrows the CHECK back down
/// once nothing writes the old spelling.</para>
///
/// <para>The MIGRATE step — this enum — was skipped, and the consequence was total: the moment 0012 ran,
/// EF could not materialise a single provider row (<c>Cannot convert string value 'Radiology' … to any value
/// in the mapped 'ProviderType' enum</c>), so every read of <c>provider.provider</c> answered 500. The
/// Providers Directory, contracts, locations and the routing lookups all went down together, and the only
/// visible symptom was a generic "the service couldn't complete this request".</para>
///
/// <para><c>Imaging</c> STAYS until 0013 is applied. Removing it now would break the reverse direction — any
/// row or payload still carrying the old spelling — which is the whole reason the contract step is deferred.</para>
/// </remarks>
public enum ProviderType { Hospital, Clinic, Lab, Pharmacy, Imaging, Radiology }

/// <summary>Canonical provider lifecycle status (22 §5.1). Only <see cref="Active"/> is routable.</summary>
public enum ProviderStatus { Active, Suspended, Terminated }

/// <summary>Explicit, auditable onboarding state machine (phase 2b.2). Draft→…→Activated, then the
/// post-activation states Suspended/Terminated mirror <see cref="ProviderStatus"/>.</summary>
public enum OnboardingState { Draft, DocumentsCollected, Credentialed, Contracted, Activated, Suspended, Terminated }

public enum ContractStatus { Draft, Active, Expired, Terminated }

/// <summary>What a contract line is priced for. <c>Radiology</c> is the successor spelling of <c>Imaging</c>
/// and both are members for the duration of the expand/contract window — see <see cref="ProviderType"/>.
///
/// <para>This one had not broken yet only because no contract line in the dev data carries the new spelling.
/// 0012 rewrites <c>contract_service_line.service_type</c> in the same statement it rewrites the provider, so
/// the first radiology contract line would have taken pricing down the same way.</para></summary>
public enum ServiceType { Lab, Imaging, Consult, Procedure, Radiology }

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

    // --- 19.9 (0015) administrative identity ------------------------------------------------------
    /// <summary>The name on the building, when it differs from the name on the contract.</summary>
    public string? CommercialName { get; set; }
    public string? TaxId { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }

    /// <summary>Why the provider is in its CURRENT standing, and who put it there. The audit chain records
    /// that the change happened and is read by Compliance; this is the operational record, read by the team
    /// that has to decide next month whether to switch them back on.</summary>
    public string? StatusReason { get; set; }
    public string? StatusActor { get; set; }
    public string? StatusActorName { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }

    public string? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public string? UpdatedBy { get; set; }
    public string? UpdatedByName { get; set; }

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

    // --- 19.9 (0015) ------------------------------------------------------------------------------
    /// <summary>A deactivated location is soft-deleted WITH its reason. Routing already sent patients to
    /// this address; "it is gone" and "it is gone because the lease ended in March" are different answers
    /// to the person holding an appointment card for it.</summary>
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? DeactivatedBy { get; set; }
    public string? DeactivationReason { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public string? UpdatedByName { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
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

    // --- 19.9 (0015) ------------------------------------------------------------------------------
    public string? StatusReason { get; set; }
    public string? StatusActor { get; set; }
    public string? StatusActorName { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public string? UpdatedBy { get; set; }
    public string? UpdatedByName { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

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

/// <summary>Lifecycle of a request to terminate a provider (2026-08-09 audit).</summary>
public enum TerminationRequestStatus { Requested, Approved, Withdrawn, Superseded }

/// <summary>
/// A pending, dual-controlled provider termination.
/// </summary>
/// <remarks>
/// <para>Termination flips a provider out of the routable network, revokes every provider-scoped user's
/// access and publishes both facts platform-wide. It was advertised as dual-controlled and enforced by
/// comparing the actor's subject against a <c>secondApproverSubject</c> STRING in the request body — a value
/// the person terminating typed themselves. The named approver never authenticated, never consented, and was
/// never checked to exist.</para>
///
/// <para>The approver is now whoever holds the bearer token on the second call, mirroring the admin
/// break-glass flow (Requested → Approved) that this platform already implements correctly. "Who agreed to
/// this" becomes something the system observed rather than something the requester asserted.</para>
/// </remarks>
public sealed class ProviderTerminationRequest
{
    public Guid RequestId { get; set; }
    public string TenantId { get; set; } = default!;
    public Guid ProviderId { get; set; }
    public string Reason { get; set; } = default!;
    public TerminationRequestStatus Status { get; set; } = TerminationRequestStatus.Requested;
    public string RequestedBy { get; set; } = default!;
    public DateTimeOffset RequestedAt { get; set; }
    /// <summary>The subject that APPROVED, read from their own token — never supplied by the requester.</summary>
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
}


// --- 19.9 (0015) history twins -------------------------------------------------------------------
//
// One append-only snapshot row per administered table. The application NEVER inserts into these; the
// trigger does, which is what makes them a record of what happened rather than of what somebody
// remembered to log.
//
// They are three separate classes rather than one base with three subclasses because EF maps an
// inheritance hierarchy as one table by default, and three tables sharing a C# base is exactly the shape
// that turns into a silent TPH mapping nobody asked for.

/// <summary>Snapshots of <c>provider.provider</c>. The table has existed since 0001; 0015 gave it the
/// tenant column and the row-level security it needed before anything was allowed to read it.</summary>
public sealed class ProviderHistoryRow
{
    public long HistoryId { get; set; }
    public Guid ProviderId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public string RowSnapshot { get; set; } = default!;
    /// <summary>0001 named this column <c>changed_at</c>; the later twins use <c>recorded_at</c>. The
    /// column name is not worth a rename migration on a table this size — the projection normalises it.</summary>
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class ProviderLocationHistoryRow
{
    public long HistoryId { get; set; }
    public Guid LocationId { get; set; }
    public Guid ProviderId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public string RowSnapshot { get; set; } = default!;
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProviderContractHistoryRow
{
    public long HistoryId { get; set; }
    public Guid ContractId { get; set; }
    public Guid ProviderId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public string RowSnapshot { get; set; } = default!;
    public DateTimeOffset RecordedAt { get; set; }
}
