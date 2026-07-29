namespace Mersal.Emr.Domain;

/// <summary>Appointment state machine (23-state-machines §6), restricted to the canonical persisted status
/// set {Booked, CheckedIn, Completed, NoShow, Cancelled}. Every mutating endpoint (3.1 book, 3.2
/// reschedule/cancel/no-show, phase-2 check-in, phase-4 encounter close) routes its transition through here
/// so illegal moves are rejected uniformly as an audited 409 (<c>TransitionDenied</c>).</summary>
public static class AppointmentWorkflow
{
    /// <summary>Whether a slot is HELD (and must be released on exit) while an appointment is in this status.</summary>
    public static bool HoldsSlot(AppointmentStatus status)
        => status is AppointmentStatus.Booked or AppointmentStatus.CheckedIn;

    /// <summary>Legal transition table. Booking (—→Booked) is the creation path and is not listed here.</summary>
    public static bool CanTransition(AppointmentStatus from, AppointmentStatus to) => (from, to) switch
    {
        (AppointmentStatus.Booked, AppointmentStatus.CheckedIn) => true,   // beneficiary arrives (phase-2 gate)
        (AppointmentStatus.Booked, AppointmentStatus.Cancelled) => true,   // cancel
        (AppointmentStatus.Booked, AppointmentStatus.NoShow) => true,      // grace passed, absent
        (AppointmentStatus.CheckedIn, AppointmentStatus.Completed) => true,// encounter closed (phase 4)
        (AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled) => true,// cancel after check-in
        // 18.A4: rebook a no-show (23 §6 "NoShow --rebook--> Scheduled", within re-booking policy). This
        // declared transition was missing, so a beneficiary who missed an appointment could not be put
        // back on the same record — the only route was a brand-new appointment, which loses the link to
        // the missed one and hides repeat no-shows from the Case Manager escalation the spec asks for.
        (AppointmentStatus.NoShow, AppointmentStatus.Booked) => true,
        _ => false,
    };

    /// <summary>How long after the scheduled END a Booked appointment must sit before the desk may mark it a
    /// no-show. Lives here, with the rule that uses it, so the flag the UI renders and the check the endpoint
    /// enforces cannot drift apart — a button offered a minute early is a 409 the receptionist cannot explain.</summary>
    public static readonly TimeSpan NoShowGrace = TimeSpan.FromMinutes(15);

    /// <summary>No-show guard (US-022): only a still-Booked appointment whose scheduled window has passed
    /// (beyond the grace period) and that never checked in may be marked NoShow.</summary>
    public static bool CanNoShow(Appointment appt, DateTimeOffset now, TimeSpan grace)
        => appt.Status == AppointmentStatus.Booked && now >= appt.ScheduledEnd + grace;

    /// <summary>Reschedule keeps the appointment Booked but swaps the held slot; only a Booked appointment
    /// may be rescheduled (3.2).</summary>
    public static bool CanReschedule(AppointmentStatus from) => from == AppointmentStatus.Booked;

    /// <summary>Cancellation is legal from any slot-holding status.</summary>
    public static bool CanCancel(AppointmentStatus from) => HoldsSlot(from);
}

/// <summary>Human labels for appointment types (UI/reporting).</summary>
public static class AppointmentTypeLabels
{
    public static string Label(AppointmentType t) => t switch
    {
        AppointmentType.WalkIn => "Walk-in",
        AppointmentType.Scheduled => "Scheduled",
        AppointmentType.Referral => "Referral",
        AppointmentType.FollowUp => "Follow-up",
        _ => t.ToString(),
    };

    /// <summary>Referral bookings must carry a REF-* link; follow-ups must carry an originating encounter
    /// (US-020, 23 §4 ReferralScheduled). Scheduled/Walk-in need neither.</summary>
    public static bool LinkageSatisfied(AppointmentType type, string? referralRef, Guid? originEncounterId) => type switch
    {
        AppointmentType.Referral => !string.IsNullOrWhiteSpace(referralRef),
        AppointmentType.FollowUp => originEncounterId is not null,
        _ => true,
    };
}
