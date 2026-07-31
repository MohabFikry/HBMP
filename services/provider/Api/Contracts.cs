using Mersal.Provider.Domain;

namespace Mersal.Provider.Api;

// Request/response DTOs. agreed_price is T2 financial — response projections mask it unless the caller
// holds the provider:finance scope (see PriceView in Program.cs).

public sealed record CreateProvider(string ProviderCode, string LegalName, string ProviderType);

public sealed record CreateLocation(string Name, string? Governorate, string? Address, decimal? GeoLat, decimal? GeoLng, bool IsPrimary);

public sealed record CreateContract(string ContractNo, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record AddServiceLine(string ServiceType, string CodeSystem, string Code, decimal AgreedPrice, string? CurrencyCode);

public sealed record AddCredential(string CredentialType, DateOnly? ValidFrom, DateOnly? ValidTo, Guid? DocumentId, bool IsMandatory);

public sealed record ProviderView(
    Guid ProviderId, string ProviderCode, string LegalName, string ProviderType, string ProviderTypeLabel,
    string Status, string OnboardingState);

public sealed record CapabilityView(string ServiceType, string CodeSystem, string Code, decimal? AgreedPrice, string? CurrencyCode);

// --- 2b.2 onboarding ---------------------------------------------------------------------------
public sealed record ProvisionUser(string SubjectRef, string Role);

/// <summary>Suspend/terminate carry a mandatory reason (audited). Terminate is dual-controlled: the
/// second approver must differ from the acting user (SoD).</summary>
public sealed record StateChange(string Reason, string? SecondApproverSubject);

// --- 14.1 branch (internal Mersal facilities; org reference data, no PHI) -----------------------
public sealed record CreateBranch(
    string BranchCode, string NameEn, string NameAr, string? City, string? Address,
    string? Timezone, string? Phone, string? OpeningHours);

public sealed record UpdateBranch(
    string? NameEn, string? NameAr, string? City, string? Address, string? Timezone, string? Phone, string? OpeningHours);

/// <summary>Branch status change carries a mandatory reason (audited).</summary>
public sealed record ChangeBranchStatus(string Status, string Reason);

public sealed record BranchView(
    Guid BranchId, string BranchCode, string NameEn, string NameAr, string? City, string? Address,
    string Timezone, string? Phone, string? OpeningHours, string Status);

// --- 14.5 practitioner, specialty & branch assignment -----------------------------------------
public sealed record CreatePractitioner(
    string UserId, string PractitionerType, string FullNameEn, string FullNameAr, string? LicenseNo, DateOnly? LicenseExpiry);

public sealed record AssignSpecialty(string SpecialtyCode, bool IsPrimary);

public sealed record AssignPractitionerBranch(Guid BranchId, DateOnly ValidFrom, DateOnly? ValidTo);

/// <summary>25.2 — record or renew a practitioner's licence. BOTH fields are required: an expiry date is what
/// makes the licence enforceable as at a slot date (25.3), so a licence stored without one would be a field
/// that looks maintained and gates nothing.</summary>
public sealed record UpdatePractitionerLicence(string LicenseNo, DateOnly? LicenseExpiry);

/// <summary>Remove a NON-primary specialty. Revoking the primary is refused (409) — promote another first,
/// because a practitioner without a primary specialty is invisible to every booking picker.</summary>
public sealed record RevokeSpecialty(string SpecialtyCode);

/// <summary>End a practitioner's assignment to a branch: the active rows become <c>Revoked</c> rather than
/// being deleted, so the history that explains an earlier booking there survives.</summary>
public sealed record RevokePractitionerBranch(Guid BranchId);

/// <summary>Active | Suspended | Inactive, with a reason (audited).</summary>
public sealed record ChangePractitionerStatus(string Status, string Reason);

/// <summary>Picker/min-necessary practitioner view. <c>LicenseNo</c> is null unless the caller is an admin
/// (provider:write) — no licence numbers to booking clinicians (design 37 §4).</summary>
public sealed record PractitionerView(
    Guid PractitionerId, string PractitionerType, string FullNameEn, string FullNameAr,
    string? PrimarySpecialty, IReadOnlyList<string> Specialties, IReadOnlyList<Guid> Branches,
    string Status, string? LicenseNo);

