namespace Mersal.Provider.Domain;

/// <summary>
/// 25.3 (design 42 §3) — the licence gate. `license_no` and `license_expiry` have existed on
/// <see cref="Practitioner"/> since migration 0006 and NOTHING read them: bookability checked practitioner
/// status and branch assignment only, so a doctor whose licence expired last year was still bookable. This
/// is the rule that closes it, and it is a pure function so every caller — provider's probe, emr's slot
/// generation, emr's booking validator, the expiry sweeper — asks the same question the same way.
/// </summary>
public static class PractitionerLicence
{
    /// <summary>RFC 7807 type for a refusal caused by an expired licence, shared by emr's two booking gates
    /// so a client can distinguish "not licensed on that date" from "does not work at that branch".</summary>
    public const string ExpiredProblemType = "urn:hbmp:practitioner-licence-expired";

    /// <summary>
    /// Is this licence valid on <paramref name="asOf"/>?
    ///
    /// <para><b>THE BOUNDARY IS INCLUSIVE, and the choice is deliberate.</b> A licence stamped "expires
    /// 30 September" is valid THROUGH 30 September; a doctor is not unlicensed on the last day printed on
    /// their own certificate. Exclusive would silently move every practitioner's last working day one day
    /// earlier than the regulator's, which is the kind of off-by-one that surfaces as a clinic cancelling a
    /// full day of appointments nobody can explain. Asserted on both boundary days
    /// (<c>LicenceValidityTests</c>).</para>
    ///
    /// <para><b>A NULL expiry is NOT expired.</b> "No expiry recorded" is missing data, not a lapsed licence,
    /// and the two must not collapse into one answer. Nurses are recorded without a licence at all, and on
    /// the day this shipped most practitioner rows carried no expiry — treating null as expired would have
    /// emptied every clinic's calendar at once and been read, correctly, as an outage rather than a control.
    /// The pressure is applied at the point of entry instead: <c>POST /practitioners/{id}/licence</c> refuses
    /// to store a licence number without an expiry date, so anything recorded from here on IS enforceable.
    /// <see cref="IsEnforceable"/> exists to let a worklist surface the remaining gaps honestly.</para>
    /// </summary>
    public static bool IsValidAt(DateOnly? licenseExpiry, DateOnly asOf) =>
        licenseExpiry is not { } expiry || expiry >= asOf;

    /// <summary>True when a licence carries an expiry date and can therefore actually be gated on. A licence
    /// number with no expiry passes <see cref="IsValidAt"/> at every date — it is recorded, not enforced —
    /// and that distinction belongs on a coordinator's screen rather than buried in a boolean.</summary>
    public static bool IsEnforceable(string? licenseNo, DateOnly? licenseExpiry) =>
        !string.IsNullOrWhiteSpace(licenseNo) && licenseExpiry is not null;

    /// <summary>Whole days from <paramref name="asOf"/> until the licence lapses; negative once it has.
    /// Null when there is no expiry to count towards.</summary>
    public static int? DaysUntilExpiry(DateOnly? licenseExpiry, DateOnly asOf) =>
        licenseExpiry is { } expiry ? expiry.DayNumber - asOf.DayNumber : null;

    /// <summary>
    /// The warning thresholds, following the existing <c>ProviderCredentialExpiring</c> precedent: 90, 60 and
    /// 30 days out, then the day itself. Descending so the first match is the widest window not yet passed.
    /// </summary>
    public static readonly IReadOnlyList<int> WarningDays = [90, 60, 30];

    /// <summary>The threshold this licence crosses ON <paramref name="asOf"/>, or null on every other day.
    /// Exact-day matching is what makes the sweeper idempotent without a "last warned" column: running it
    /// twice in one day emits the same threshold and the consumer dedupes on the event id, while running it
    /// on the following day emits nothing until the next threshold.</summary>
    public static int? WarningThresholdCrossedOn(DateOnly? licenseExpiry, DateOnly asOf)
    {
        if (DaysUntilExpiry(licenseExpiry, asOf) is not { } days) return null;
        return WarningDays.Contains(days) ? days : null;
    }

    /// <summary>Human-readable refusal for the 422s, carrying the expiry date — the one fact that tells the
    /// desk whether to wait for a renewal or find cover.</summary>
    public static string ExpiredDetail(Guid practitionerId, DateOnly expiry, DateOnly asOf) =>
        $"Practitioner {practitionerId} held no valid licence on {asOf:yyyy-MM-dd}: it expired on {expiry:yyyy-MM-dd}.";
}
