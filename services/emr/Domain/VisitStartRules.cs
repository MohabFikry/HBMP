namespace Mersal.Emr.Domain;

/// <summary>Whether a visit may be started from an appointment (23 §1). Pure, so the rule is testable on its
/// own and the endpoint reads as the decision it makes rather than the conditions it assembles.</summary>
public static class VisitStartRules
{
    /// <summary>A visit starts from a CheckedIn appointment, and — when the appointment names a practitioner —
    /// only for that practitioner. A NULL <c>DoctorId</c> is a general clinic session that belongs to whoever
    /// is on shift. An unidentifiable caller is refused a named appointment rather than waved through.</summary>
    public static bool MayStart(Appointment appt, Guid? caller)
    {
        ArgumentNullException.ThrowIfNull(appt);
        if (appt.Status != AppointmentStatus.CheckedIn) return false;
        if (appt.DoctorId is not { } assigned) return true;
        return caller is { } c && c == assigned;
    }
}
