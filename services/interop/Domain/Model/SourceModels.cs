namespace Mersal.Interop.Domain.Model;

/// <summary>
/// The MINIMIZED internal projections the FHIR mappers consume — only the fields §12 maps to R4. These are read
/// from the owning services' native APIs under the caller's bearer token (so field-level minimum-necessary and
/// ABAC are enforced at the source, not re-implemented here). The façade owns none of this data.
/// </summary>
public sealed record SourceIdentifier(string Type, string Value);

public sealed record SourceTelecom(string System, string Value, string? Use = null);

public sealed record SourceAddress(string? Line, string? City, string? District, string? Country);

public sealed record BeneficiarySource(
    string Id,
    IReadOnlyList<SourceIdentifier> Identifiers,
    string? FamilyName,
    string? GivenName,
    DateOnly? BirthDate,
    string? Gender,
    IReadOnlyList<SourceTelecom> Telecoms,
    IReadOnlyList<SourceAddress> Addresses);

public sealed record CoverageCostShare(string Type, decimal Amount, string Currency);
public sealed record CoverageLimit(string Category, decimal? Limit, decimal? Remaining);

public sealed record CoverageSource(
    string Id,
    string BeneficiaryId,
    string? Status,
    string? PayorName,
    string? ClassCategory,
    string? ClassValue,
    IReadOnlyList<CoverageCostShare> CostToBeneficiary,
    IReadOnlyList<CoverageLimit> Limits);

public sealed record CodedConcept(string System, string Code, string? Display);

public sealed record ServiceRequestSource(
    string Id,
    string HbmpStatus,
    string Intent,          // "order" | "referral"
    string? Category,       // "laboratory" | "imaging" | "referral"
    CodedConcept? Code,
    decimal? Quantity,
    string? QuantityUnit,
    string BeneficiaryId,
    string? RequesterId,
    string? PerformerId);

public sealed record MedicationRequestSource(
    string Id,
    string HbmpStatus,
    CodedConcept? Medication,
    string? DosageText,
    decimal? DispenseQuantity,
    string? DispenseUnit,
    string BeneficiaryId,
    string? RequesterId);

public sealed record DiagnosticReportSource(
    string Id,
    string HbmpStatus,
    CodedConcept? Code,
    string BeneficiaryId,
    string? ServiceRequestId,
    DateTimeOffset? Issued,
    string? PresentedFormContentType,
    string? PresentedFormTitle);

public sealed record EncounterSource(
    string Id,
    string HbmpStatus,
    string? ClassCode,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string BeneficiaryId,
    string? PractitionerId);

public sealed record ConditionSource(
    string Id,
    string? ClinicalStatus,
    CodedConcept? Code,
    string BeneficiaryId,
    string? EncounterId,
    DateTimeOffset? RecordedDate);

public sealed record ObservationSource(
    string Id,
    string HbmpStatus,
    string? Category,       // "vital-signs" | "laboratory"
    CodedConcept? Code,
    decimal? Value,
    string? Unit,
    string? UnitCode,
    string BeneficiaryId,
    string? EncounterId,
    DateTimeOffset? Effective);

public sealed record AllergyIntoleranceSource(
    string Id,
    CodedConcept? Code,
    string? Criticality,
    string? Reaction,
    string BeneficiaryId);
