namespace Mersal.Emr.Domain;

// Status-driven visit gating (23-state-machines §1 & §6, US-011). Only an Active member may start an
// encounter; every other status is blocked with actionable guidance and nothing is persisted.

/// <summary>Member lifecycle status as read from eligibility/patient (mirror of the patient enum).</summary>
public enum MemberStatus { Pending, Active, Suspended, Expired, Blocked, Inactive }

public sealed record GateResult(bool Allowed, string? Guidance);

public static class VisitGate
{
    /// <summary>Evaluate whether a visit may be started. Active → allowed; anything else → blocked
    /// with guidance that routes the receptionist to the right remediation.</summary>
    public static GateResult Evaluate(MemberStatus status) => status switch
    {
        MemberStatus.Active => new GateResult(true, null),
        MemberStatus.Pending => new GateResult(false, "Registration is not yet activated — complete activation before starting a visit."),
        MemberStatus.Suspended => new GateResult(false, "Membership is suspended — refer to the Case Manager before proceeding."),
        MemberStatus.Expired => new GateResult(false, "Coverage has expired — refer to the Case Manager to renew before a visit."),
        MemberStatus.Blocked => new GateResult(false, "Membership is blocked — a director override is required."),
        MemberStatus.Inactive => new GateResult(false, "Membership is inactive — reinstate via registration before a visit."),
        _ => new GateResult(false, "Member status does not permit a visit — refer to the Case Manager."),
    };
}
