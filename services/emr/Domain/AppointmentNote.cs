namespace Mersal.Emr.Domain;

/// <summary>
/// The rules for a booking note (14.5 / phase 3.1).
///
/// <para>A note is a short administrative arrangement — "wheelchair access", "interpreter: Tigrinya", "sister
/// attending". It is written by reception or the call centre and read by both plus the treating doctor.</para>
///
/// <para><b>Why this is a domain type rather than a plain string on the request.</b> The note crosses a
/// boundary the platform otherwise enforces hard: the call centre may write it and a doctor may read it,
/// while the call centre is deliberately given no clinical surface anywhere else. The length cap is the one
/// structural thing standing between "an arrangement" and "an unaudited clinical record written by someone
/// with no treating relationship", so it belongs somewhere it is applied identically on every path — booking
/// today, and whatever writes a note next — rather than being re-implemented per endpoint.</para>
/// </summary>
public static class AppointmentNote
{
    /// <summary>Enough for an arrangement, not enough for a history. Mirrored by <c>varchar(500)</c> in
    /// migration 0011 so the database refuses an over-long note even if a future writer forgets to.</summary>
    public const int MaxLength = 500;

    public const string TooLongProblemType = "urn:hbmp:note-too-long";

    /// <summary>Trim to what should actually be stored. Whitespace-only becomes <c>null</c>: an empty note and
    /// no note are the same fact, and storing "" would make the UI render an empty note icon on an
    /// appointment nobody wrote a note for.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim();
    }

    /// <summary>The reason to refuse, or null when acceptable. Refuses rather than truncating: silently
    /// cutting an operator's note at 500 characters loses the end of a sentence they believed they had
    /// recorded, and they have no way to discover it.</summary>
    public static string? Refuse(string? normalized) =>
        normalized is { Length: > MaxLength }
            ? $"A booking note may be at most {MaxLength} characters — this one is {normalized.Length}. " +
              "Booking notes are for access needs and arrangements, not clinical detail."
            : null;
}
