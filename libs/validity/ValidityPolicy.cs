namespace Mersal.Validity;

/// <summary>
/// The kinds of clinical instruction that go stale, and therefore carry an expiry.
/// </summary>
/// <remarks>
/// <para>
/// A prescription and an investigation order are both a clinician's decision about a patient AT A MOMENT.
/// They are acted on later, by someone else, who cannot see whether the reasoning still holds — the patient
/// may have recovered, deteriorated, been prescribed something that interacts, or died. An instruction with
/// no expiry says the opposite: that it is as good today as it was the day it was written.
/// </para>
/// <para>
/// The four are configured SEPARATELY rather than sharing one number. A course of antibiotics and a follow-up
/// MRI do not go stale at the same rate, and a single setting cannot be split later without asking every
/// tenant what they actually meant by it — whereas four settings can trivially be made equal.
/// </para>
/// </remarks>
public enum ValidityArtefact
{
    Prescription,
    LabOrder,
    ImagingOrder,
    /// <summary>Procedure orders share the order pipeline and would otherwise be the one thing that never
    /// expires — an invisible exception nobody chose. Configured like the rest.</summary>
    ProcedureOrder,
}

/// <summary>
/// Where the validity periods live, what they default to, and the bounds a supervisor may set them within.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than duplicated per service on purpose. The key strings and the fallback are the two things
/// that MUST NOT drift: a service that reads <c>validity.rx.days</c> while admin writes
/// <c>validity.prescription.days</c> gets the default for ever and reports no error, and a service whose
/// fallback is 30 while another's is 10 makes the configured number a suggestion.
/// </para>
/// <para>
/// Stored as <c>admin.system_config</c> rows — an existing effective-dated, typed, versioned and audited
/// store whose own documentation names thresholds of exactly this kind. Nothing here needed a new table.
/// </para>
/// </remarks>
public static class ValidityPolicy
{
    /// <summary>
    /// The value in force when nobody has set one.
    ///
    /// <para>Ten days, per the platform default. It is deliberately a CONSTANT and not a nullable "unset":
    /// every path that fails to read configuration falls back to this, never to "no expiry". An unexpiring
    /// prescription is precisely the state this feature exists to prevent, so it must not be reachable by a
    /// config outage, a missing row, a new tenant, or a typo in a key.</para>
    /// </summary>
    public const int DefaultDays = 10;

    /// <summary>Floor of 1: a validity of zero days would expire everything at the moment of writing.</summary>
    public const int MinDays = 1;

    /// <summary>
    /// Ceiling of 365.
    ///
    /// <para>Not a technical limit — a clinical one. Beyond a year the expiry has stopped being a safety
    /// control and become a formality, and a supervisor who types 3650 by accident should be stopped rather
    /// than quietly given a decade.</para>
    /// </summary>
    public const int MaxDays = 365;

    /// <summary>The <c>system_config</c> key holding this artefact's validity, in days.</summary>
    public static string KeyFor(ValidityArtefact artefact) => artefact switch
    {
        ValidityArtefact.Prescription => "validity.prescription.days",
        ValidityArtefact.LabOrder => "validity.lab-order.days",
        ValidityArtefact.ImagingOrder => "validity.imaging-order.days",
        ValidityArtefact.ProcedureOrder => "validity.procedure-order.days",
        _ => throw new ArgumentOutOfRangeException(nameof(artefact), artefact, "Unknown validity artefact."),
    };

    /// <summary>Every artefact, for the supervisor screen and the read endpoint.</summary>
    public static IReadOnlyList<ValidityArtefact> All { get; } =
        [ValidityArtefact.Prescription, ValidityArtefact.LabOrder, ValidityArtefact.ImagingOrder, ValidityArtefact.ProcedureOrder];

    public static bool IsInRange(int days) => days >= MinDays && days <= MaxDays;

    /// <summary>
    /// Parse a stored config value into a usable number of days.
    ///
    /// <para>Anything unparseable or out of range resolves to <see cref="DefaultDays"/> rather than throwing.
    /// A malformed row is an operator error that must not stop clinicians prescribing — but it must not grant
    /// an unbounded validity either, so it lands on the conservative value.</para>
    /// </summary>
    public static int DaysFrom(string? configuredValue) =>
        int.TryParse(configuredValue, out var d) && IsInRange(d) ? d : DefaultDays;

    /// <summary>
    /// When something written at <paramref name="issuedAt"/> stops being actionable.
    ///
    /// <para>End of the last valid DAY in the clinic's own time zone, not the same clock time N days later.
    /// A prescription written at 16:50 and one written at 09:05 on the same morning are valid for the same
    /// number of days; expiring the first at 16:50 would cut a day off it for no reason a patient could be
    /// told. Cairo, per the platform display rule.</para>
    /// </summary>
    public static DateTimeOffset ExpiryFor(DateTimeOffset issuedAt, int validityDays)
    {
        var cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        var local = TimeZoneInfo.ConvertTime(issuedAt, cairo);
        // Midnight at the END of the last valid day: issued today with 10 days = expires at 00:00 on day 11.
        var endOfLastDay = local.Date.AddDays(validityDays);
        var offset = cairo.GetUtcOffset(endOfLastDay);
        // Returned in UTC. The Cairo anchoring above decides WHICH INSTANT the day ends at; the instant is
        // then stored and compared as UTC like every other timestamp on the platform — and Npgsql refuses a
        // non-zero offset for `timestamptz` outright, so a Cairo-offset value would fail at the INSERT.
        return new DateTimeOffset(endOfLastDay, offset).ToUniversalTime();
    }
}
