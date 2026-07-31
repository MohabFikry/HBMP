namespace Mersal.Emr.Domain;

/// <summary>
/// THE availability computation (design 42 §4/§7 rule 5). Pure derivation of bookable slots from the weekly
/// recurring rule (23 §6 "recurring availability → bookable slots"), side-effect-free so it is unit-tested
/// without a database; the Infrastructure layer persists what this returns.
///
/// <para><b>Availability is computed in exactly one place.</b> Every consumer — the doctor picker,
/// <c>GET /booking/doctor-availability</c>, <c>GET /appointment-days</c>, slot materialization and the
/// booking validator — resolves through this function. If you find a second place deciding whether a slot
/// exists, that is the bug, not an optimisation. The way that failure presents is a patient given an
/// appointment with a doctor who is on leave.</para>
///
/// <para>The full intersection, from design 42 §4:</para>
/// <code>recurring rule − exceptions ∩ active branch assignment ∩ valid licence ∩ practitioner Active</code>
/// <para>The last two terms arrive as DATE BOUNDS (<c>bookableUntil</c>) rather than as booleans, because
/// they are date-varying and this function generates across a range: a licence expiring on 30 September and
/// an assignment ending on 15 October both truncate the same run, at different points. "Practitioner Active"
/// is the one term that is NOT date-varying — a suspension takes effect immediately — so it stays a
/// precondition at the endpoint, which refuses the whole request rather than generating nothing.</para>
/// </summary>
public static class SlotGeneration
{
    /// <summary>
    /// Materialize the concrete slots that <paramref name="availability"/> yields across the inclusive date
    /// range [<paramref name="fromDate"/>, <paramref name="toDate"/>]. A slot is emitted only if it fits
    /// WHOLLY inside its window; a trailing partial remainder is dropped. Times are interpreted in
    /// <paramref name="offset"/> (Africa/Cairo at the call site).
    /// </summary>
    /// <param name="bookableUntil">
    /// 25.3/25.4 — the last date this practitioner may lawfully be booked: the EARLIER of their licence
    /// expiry and the end of their branch assignment, or null when neither bounds them. Applied PER DATE
    /// inside the loop, not as a precondition on the whole call: a licence expiring on 30 September must
    /// yield September slots and no October ones. Refusing the whole request would make a coordinator
    /// generate two ranges by hand and guess the boundary; generating past it would put patients in front of
    /// a practitioner who may not lawfully see them. INCLUSIVE, matching
    /// <c>PractitionerLicence.IsValidAt</c> — a doctor is not unlicensed on the last day printed on their
    /// own certificate.
    /// </param>
    /// <param name="exceptions">
    /// 25.4 — leave, holidays, closures and ad-hoc clinics. Subtractive kinds remove slots from a day the
    /// weekly pattern covers; <see cref="RosterExceptionKind.AdHocClinic"/> ADDS a window on a date the
    /// pattern does not cover at all. Passing none reproduces the pre-25.4 behaviour exactly.
    /// </param>
    public static IReadOnlyList<AppointmentSlot> Generate(
        ProviderAvailability availability, DateOnly fromDate, DateOnly toDate, TimeSpan offset,
        DateOnly? bookableUntil = null,
        IReadOnlyCollection<RosterException>? exceptions = null)
    {
        ArgumentNullException.ThrowIfNull(availability);
        if (availability.SlotMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(availability), "SlotMinutes must be positive.");
        if (availability.EndTime <= availability.StartTime) throw new ArgumentException("EndTime must be after StartTime.", nameof(availability));

        var slots = new List<AppointmentSlot>();
        var step = TimeSpan.FromMinutes(availability.SlotMinutes);
        var all = exceptions ?? [];

        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            // The licence/assignment bound is a CLINIC-CALENDAR comparison, which is why it lives here rather
            // than against the UTC instants below: `date` is the day the clinic is open, and that is the day
            // the regulator's certificate and the assignment window are about.
            if (bookableUntil is { } until && date > until) continue;

            var applicable = all
                .Where(e => e.AppliesTo(date, availability.BranchId, availability.DoctorId))
                .ToList();

            // The windows this date offers, before subtraction:
            //   • the weekly pattern, when the day-of-week matches
            //   • plus one per AdHocClinic — which is why an ad-hoc Friday produces slots on a Friday the
            //     recurring rule says nothing about.
            var windows = new List<(TimeOnly Start, TimeOnly End)>();
            if (date.DayOfWeek == availability.DayOfWeek)
                windows.Add((availability.StartTime, availability.EndTime));
            foreach (var adHoc in applicable.Where(e => e.Kind == RosterExceptionKind.AdHocClinic))
                windows.Add((adHoc.StartTime!.Value, adHoc.EndTime!.Value));

            if (windows.Count == 0) continue;

            var subtractive = applicable.Where(e => e.IsSubtractive).ToList();

            // A WHOLE-DAY subtraction removes the day outright — including any ad-hoc clinic on it. That
            // ordering is deliberate: if the clinic is shut, an extra session at a shut clinic is not a
            // session, and the alternative (ad-hoc wins) would let a stale ad-hoc row quietly reopen a branch
            // somebody closed.
            if (subtractive.Any(e => e.IsWholeDay)) continue;

            foreach (var (windowStartTime, windowEndTime) in windows)
            {
                var windowStart = new DateTimeOffset(date.ToDateTime(windowStartTime), offset);
                var windowEnd = new DateTimeOffset(date.ToDateTime(windowEndTime), offset);

                for (var start = windowStart; start + step <= windowEnd; start += step)
                {
                    var slotFrom = TimeOnly.FromDateTime(start.DateTime);
                    var slotTo = TimeOnly.FromDateTime((start + step).DateTime);

                    // OVERLAP, not containment: a slot half inside a leave window is not half-bookable.
                    if (subtractive.Any(e => e.Covers(slotFrom, slotTo))) continue;

                    slots.Add(new AppointmentSlot
                    {
                        SlotId = Guid.NewGuid(),
                        ProviderId = availability.ProviderId,
                        LocationId = availability.LocationId,
                        // The branch has to travel with the slot: it is what lets a branch-scoped desk find
                        // the clinics it may book into. Copying provider and location but not branch left
                        // every materialized slot branchless and therefore unreachable by branch.
                        BranchId = availability.BranchId,
                        DoctorId = availability.DoctorId,
                        // Normalized to UTC. The instant is identical either way, but Npgsql refuses to write
                        // a non-zero offset to timestamptz, so emitting these at the Cairo offset made every
                        // call to POST /appointment-slots fail with an unhandled 500 — availability could only
                        // ever be inserted by hand. The window arithmetic above still happens in local
                        // wall-clock, which is the whole point of taking an offset.
                        SlotStart = start.ToUniversalTime(),
                        SlotEnd = (start + step).ToUniversalTime(),
                    });
                }
            }
        }
        return slots;
    }

    /// <summary>
    /// The last date a practitioner may be booked: the EARLIER of their licence expiry and the end of their
    /// branch assignment. Null when neither bounds them.
    ///
    /// Exists so the two bounds are combined in ONE place. Two call sites each taking a min is two places to
    /// forget one of them, and forgetting the assignment bound generates slots for a locum whose contract
    /// ended — which looks exactly like a working calendar right up until the patient arrives.
    /// </summary>
    public static DateOnly? BookableUntil(DateOnly? licenceExpiry, DateOnly? assignmentValidTo) =>
        (licenceExpiry, assignmentValidTo) switch
        {
            (null, null) => null,
            ({ } l, null) => l,
            (null, { } a) => a,
            ({ } l, { } a) => l < a ? l : a,
        };
}
