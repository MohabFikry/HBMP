namespace Mersal.Patient.Domain;

/// <summary>
/// The registration APPLICATION sub-state (distinct from beneficiary.status). The beneficiary stays
/// Pending until activation, then becomes Active (1.4, US-003). Wizard steps set the guard flags.
/// </summary>
public enum RegistrationStatus { Pending, InfoRequested, Rejected, Active }

public enum RegistrationDecision { Approve, RequestInfo, Reject }

public sealed class Registration
{
    public Guid RegistrationId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid BeneficiaryId { get; set; }
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Pending;

    /// <summary>Approval guards (US-003): documents verified AND a policy/coverage bound.</summary>
    public bool DocumentsVerified { get; set; }
    public bool CoverageBound { get; set; }

    public string? Notes { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Pure decision rules for the registration workflow (unit-tested; Api orchestrates persistence).</summary>
public static class RegistrationRules
{
    /// <summary>
    /// Validate a decision against the current state + guards. Returns an error message, or null if the
    /// decision is allowed. Approve needs docs verified + coverage bound; Reject/RequestInfo need notes.
    /// </summary>
    public static string? ValidateDecision(Registration reg, RegistrationDecision decision, string? notes)
    {
        ArgumentNullException.ThrowIfNull(reg);
        if (reg.Status is RegistrationStatus.Active or RegistrationStatus.Rejected)
            return $"registration is already {reg.Status}";

        return decision switch
        {
            RegistrationDecision.Reject when string.IsNullOrWhiteSpace(notes) => "a reason is required to reject",
            RegistrationDecision.RequestInfo when string.IsNullOrWhiteSpace(notes) => "notes describing the missing information are required",
            RegistrationDecision.Approve when !reg.DocumentsVerified => "cannot approve: documents are not verified",
            RegistrationDecision.Approve when !reg.CoverageBound => "cannot approve: no policy/coverage is bound",
            _ => null,
        };
    }

    /// <summary>The registration status resulting from an (already-validated) decision.</summary>
    public static RegistrationStatus ResultOf(RegistrationDecision decision) => decision switch
    {
        RegistrationDecision.Approve => RegistrationStatus.Active,
        RegistrationDecision.RequestInfo => RegistrationStatus.InfoRequested,
        RegistrationDecision.Reject => RegistrationStatus.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };
}
