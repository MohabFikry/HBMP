namespace Mersal.Interop.Domain.Fhir;

/// <summary>
/// HBMP lifecycle → FHIR R4 status mappings, exactly per 17-api-specifications §12.1 (and the aligned resource
/// state machines in 23-state-machines.md). Pure lookups; unmapped inputs fall back to the FHIR "unknown"/safe
/// value rather than throwing, so a new internal status never crashes the façade.
/// </summary>
public static class StatusMaps
{
    /// <summary>ServiceRequest.status (§12.1 table verbatim).</summary>
    public static string ServiceRequest(string? hbmp) => (hbmp ?? "").Trim() switch
    {
        "Requested" or "PendingApproval" => "draft",
        "Approved" or "Active" => "active",
        "PartiallyUsed" => "active",
        "Completed" => "completed",
        "Rejected" or "Cancelled" => "revoked",
        "Expired" => "revoked",
        _ => "unknown",
    };

    /// <summary>MedicationRequest.status.</summary>
    public static string MedicationRequest(string? hbmp) => (hbmp ?? "").Trim() switch
    {
        "Requested" or "PendingApproval" => "draft",
        "Approved" or "Active" => "active",
        "PartiallyUsed" => "active",
        "Completed" or "Dispensed" => "completed",
        "Rejected" or "Cancelled" => "cancelled",
        "Expired" => "stopped",
        _ => "unknown",
    };

    /// <summary>Condition.clinicalStatus code (condition-clinical system).</summary>
    public static string ConditionClinical(string? hbmp) => (hbmp ?? "").Trim() switch
    {
        "Active" or "Confirmed" => "active",
        "Resolved" => "resolved",
        "Inactive" => "inactive",
        "Remission" => "remission",
        "Recurrence" => "recurrence",
        _ => "active",
    };

    /// <summary>Encounter.status.</summary>
    public static string Encounter(string? hbmp) => (hbmp ?? "").Trim() switch
    {
        "Planned" or "Scheduled" or "Booked" => "planned",
        "Arrived" or "CheckedIn" => "arrived",
        "InProgress" or "InConsultation" => "in-progress",
        "Completed" or "Finished" => "finished",
        "Cancelled" or "NoShow" => "cancelled",
        _ => "unknown",
    };

    /// <summary>DiagnosticReport.status.</summary>
    public static string DiagnosticReport(string? hbmp) => (hbmp ?? "").Trim() switch
    {
        "Requested" or "Registered" => "registered",
        "Partial" or "Preliminary" => "preliminary",
        "Completed" or "Final" or "Released" => "final",
        "Amended" => "amended",
        "Cancelled" => "cancelled",
        _ => "unknown",
    };

    /// <summary>Observation.status.</summary>
    public static string Observation(string? hbmp) => (hbmp ?? "").Trim() switch
    {
        "Preliminary" => "preliminary",
        "Registered" => "registered",
        "Final" or "Completed" or "Released" => "final",
        "Amended" => "amended",
        "Cancelled" => "cancelled",
        _ => "final",
    };

    /// <summary>AllergyIntolerance.criticality.</summary>
    public static string AllergyCriticality(string? hbmp) => (hbmp ?? "").Trim() switch
    {
        "High" or "Severe" => "high",
        "Low" or "Mild" => "low",
        _ => "unable-to-assess",
    };
}
