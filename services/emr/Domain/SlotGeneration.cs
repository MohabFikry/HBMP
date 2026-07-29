namespace Mersal.Emr.Domain;

/// <summary>Pure derivation of bookable slots from a recurring availability rule (23 §6 "recurring
/// availability → bookable slots"). Kept side-effect-free so it is unit-tested without a database; the
/// Infrastructure layer persists what this returns.</summary>
public static class SlotGeneration
{
    /// <summary>Materialize the concrete slots that <paramref name="availability"/> yields across the
    /// inclusive date range [<paramref name="fromDate"/>, <paramref name="toDate"/>]. A slot is emitted only
    /// if it fits WHOLLY inside the daily window; a trailing partial remainder is dropped. Times are
    /// interpreted in <paramref name="offset"/> (Africa/Cairo at the call site).</summary>
    public static IReadOnlyList<AppointmentSlot> Generate(
        ProviderAvailability availability, DateOnly fromDate, DateOnly toDate, TimeSpan offset)
    {
        if (availability.SlotMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(availability), "SlotMinutes must be positive.");
        if (availability.EndTime <= availability.StartTime) throw new ArgumentException("EndTime must be after StartTime.", nameof(availability));

        var slots = new List<AppointmentSlot>();
        var step = TimeSpan.FromMinutes(availability.SlotMinutes);

        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            if (date.DayOfWeek != availability.DayOfWeek) continue;

            var windowStart = new DateTimeOffset(date.ToDateTime(availability.StartTime), offset);
            var windowEnd = new DateTimeOffset(date.ToDateTime(availability.EndTime), offset);

            for (var start = windowStart; start + step <= windowEnd; start += step)
            {
                slots.Add(new AppointmentSlot
                {
                    SlotId = Guid.NewGuid(),
                    ProviderId = availability.ProviderId,
                    LocationId = availability.LocationId,
                    // The branch has to travel with the slot: it is what lets a branch-scoped desk find the
                    // clinics it may book into. Copying provider and location but not branch left every
                    // materialized slot branchless and therefore unreachable by branch.
                    BranchId = availability.BranchId,
                    DoctorId = availability.DoctorId,
                    // Normalized to UTC. The instant is identical either way, but Npgsql refuses to write a
                    // non-zero offset to timestamptz, so emitting these at the Cairo offset made every call to
                    // POST /appointment-slots fail with an unhandled 500 — availability could only ever be
                    // inserted by hand. The window arithmetic above still happens in local wall-clock, which is
                    // the whole point of taking an offset.
                    SlotStart = start.ToUniversalTime(),
                    SlotEnd = (start + step).ToUniversalTime(),
                });
            }
        }
        return slots;
    }
}
