namespace Mersal.Case.Domain;

/// <summary>
/// The beneficiary-360 COORDINATION view (phase 10.1) — an EXPLICIT, field-scoped, minimum-necessary DTO assembled
/// by calling sibling services under the caller's purpose (coordination) and the case-assignment ABAC condition.
/// This is NOT a raw EMR record: per 11-permission-matrix §4 a Case Manager gets eligibility/coverage/care-plan/
/// appointments plus a CLINICAL SUMMARY where <c>diagnosis</c> is visible(coord) but emr_note / prescription /
/// lab_result / imaging_result are MASKED at summary level. The shape below can physically only carry the coordination
/// fields; there is no property for a raw clinical note or a result. Every assembly writes a PHI-read audit event.
/// </summary>
public sealed record Beneficiary360(
    Guid CaseId,
    string CaseNo,
    BeneficiaryHeader Beneficiary,
    CoverageSummary Coverage,
    CarePlanSummary CarePlan,
    IReadOnlyList<AppointmentSummary> Appointments,
    IReadOnlyList<ApprovalSummary> OpenApprovals,
    ClinicalSummary Clinical)
{
    /// <summary>The field classes exposed to the caller — the audit "fields returned" list on every 360 read.</summary>
    public static readonly string[] FieldClasses =
        ["coverage", "care_plan", "appointment", "approval_status", "diagnosis_summary"];
}

/// <summary>Masked-min beneficiary header — enough to coordinate, never the full PII record.</summary>
public sealed record BeneficiaryHeader(Guid BeneficiaryId, string DisplayName, string MaskedMemberId);

/// <summary>Eligibility + coverage summary (remaining limits) — no clinical content.</summary>
public sealed record CoverageSummary(
    string Status,                 // Eligible / Ineligible / Review
    string PolicyName,
    string CoverageCategory,
    decimal? AnnualLimit,
    decimal? RemainingLimit);

/// <summary>Care-plan summary for coordination — the plan's goals/status, not clinical notes.</summary>
public sealed record CarePlanSummary(string Status, IReadOnlyList<string> Goals, DateTimeOffset? ReviewDue);

public sealed record AppointmentSummary(Guid AppointmentId, string Clinic, DateTimeOffset When, string Status);

/// <summary>Open authorization/approval STATUS only (from approvals) — never the clinical review context.</summary>
public sealed record ApprovalSummary(string AuthNo, string Status, string Priority, DateTimeOffset? DecidedAt);

/// <summary>
/// The coordination CLINICAL SUMMARY. <see cref="ActiveDiagnoses"/> is the coord-visible diagnosis list (coded +
/// short display). Notes / prescriptions / results are represented ONLY as MASKED presence indicators (counts +
/// a "summary only" flag) — there is no field on this type that can carry their content, so detail cannot leak.
/// </summary>
public sealed record ClinicalSummary(
    IReadOnlyList<CodedDiagnosis> ActiveDiagnoses,
    MaskedSection Notes,
    MaskedSection Prescriptions,
    MaskedSection Results)
{
    public static ClinicalSummary Empty => new([], MaskedSection.None, MaskedSection.None, MaskedSection.None);
}

public sealed record CodedDiagnosis(string System, string Code, string Display);

/// <summary>A masked clinical section: how many records exist, but not their content. <see cref="SummaryOnly"/> is
/// always true — the Case Manager sees "summary only, N on file", never the record.</summary>
public sealed record MaskedSection(int Count, bool SummaryOnly)
{
    public static MaskedSection None => new(0, true);
    public static MaskedSection Of(int count) => new(count, true);
}
