using System.Text.Json.Nodes;
using Mersal.Interop.Domain.Model;

namespace Mersal.Interop.Infrastructure;

/// <summary>Result of a translated FHIR create posted to the owning service (the sibling applies its own authz,
/// validation, and audit — the façade adds no shortcut).</summary>
public sealed record SiblingWriteResult(int Status, string? CreatedId, string? RawBody)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// The seam between the FHIR façade and the core services. The façade reads/writes the internal model through
/// this interface — always under the CALLER's bearer token, so the owning service enforces field-level
/// minimum-necessary + record-level ABAC (treating-relationship, provider-ownership, sensitive-result release).
/// The façade never re-implements any of that; it maps the result to FHIR R4. <c>HttpFhirDataSource</c> is the
/// production wiring to native <c>/api/v1</c> endpoints; tests inject a deterministic fake.
/// </summary>
public interface IFhirDataSource
{
    Task<BeneficiarySource?> ReadPatientAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<BeneficiarySource>> SearchPatientsAsync(string? identifier, string? name, string? bearer, CancellationToken ct = default);

    Task<CoverageSource?> ReadCoverageAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<CoverageSource>> SearchCoverageAsync(string patientId, string? bearer, CancellationToken ct = default);

    Task<ServiceRequestSource?> ReadServiceRequestAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceRequestSource>> SearchServiceRequestsAsync(string patientId, string? bearer, CancellationToken ct = default);

    Task<MedicationRequestSource?> ReadMedicationRequestAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<MedicationRequestSource>> SearchMedicationRequestsAsync(string patientId, string? bearer, CancellationToken ct = default);

    Task<DiagnosticReportSource?> ReadDiagnosticReportAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<DiagnosticReportSource>> SearchDiagnosticReportsAsync(string patientId, string? bearer, CancellationToken ct = default);

    Task<EncounterSource?> ReadEncounterAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<EncounterSource>> SearchEncountersAsync(string patientId, string? bearer, CancellationToken ct = default);

    Task<ConditionSource?> ReadConditionAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<ConditionSource>> SearchConditionsAsync(string patientId, string? bearer, CancellationToken ct = default);

    Task<ObservationSource?> ReadObservationAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<ObservationSource>> SearchObservationsAsync(string patientId, string? bearer, CancellationToken ct = default);

    Task<AllergyIntoleranceSource?> ReadAllergyAsync(string id, string? bearer, CancellationToken ct = default);
    Task<IReadOnlyList<AllergyIntoleranceSource>> SearchAllergiesAsync(string patientId, string? bearer, CancellationToken ct = default);

    /// <summary>Translate a native command to the owning service's create endpoint (ServiceRequest/referral,
    /// MedicationRequest, Observation, AllergyIntolerance only). Returns the sibling's status + created id.</summary>
    Task<SiblingWriteResult> CreateAsync(string resourceType, JsonObject nativeCommand, string? bearer, string? idempotencyKey, CancellationToken ct = default);
}
