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

/// <summary>
/// The same Active-only rule at BOOKING time (14.5).
///
/// <para><b>Why this is not just a call to <see cref="VisitGate"/>.</b> The decision is identical — only an
/// Active member may be booked — but the remediation is not, and the guidance is the whole value of a refusal.
/// <see cref="VisitGate"/> speaks to a receptionist with the patient in front of them ("complete activation
/// before starting a visit"); this speaks to whoever is holding a phone or a queue ticket, about an
/// appointment that may be weeks away. Sharing the decision and forking the wording keeps one rule and two
/// honest messages, instead of one message that is wrong half the time.</para>
///
/// <para><b>Why Pending is refused too.</b> Booking ahead of activation looks harmless, and it is exactly how
/// someone ends up travelling to a clinic that then turns them away — the appointment carries an implicit
/// promise the platform has not made. Registration is the thing to finish first, and the guidance says so.</para>
/// </summary>
public static class BookingGate
{
    public static GateResult Evaluate(MemberStatus status) => status switch
    {
        MemberStatus.Active => new GateResult(true, null),
        MemberStatus.Pending => new GateResult(false, "Registration is not yet activated — activate the membership before booking."),
        MemberStatus.Suspended => new GateResult(false, "Membership is suspended — refer to the Case Manager before booking."),
        MemberStatus.Expired => new GateResult(false, "Coverage has expired — renew via the Case Manager before booking."),
        MemberStatus.Blocked => new GateResult(false, "Membership is blocked — a director override is required before booking."),
        MemberStatus.Inactive => new GateResult(false, "Membership is inactive — reinstate via registration before booking."),
        _ => new GateResult(false, "Member status does not permit booking — refer to the Case Manager."),
    };

    public const string ProblemType = "urn:hbmp:member-not-active";
}
