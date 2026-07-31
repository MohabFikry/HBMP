namespace Mersal.Emr.Domain;

/// <summary>
/// Phase 18.C2 (audit R2 W7 — FR-BRN-026/027) — may this practitioner hold availability at, or be booked
/// into, this branch?
///
/// A pure decision, in Domain rather than beside the endpoints, so that BOTH call sites (availability
/// creation and booking) answer identically and one test covers both. They are genuinely separate gates: a
/// walk-in is slotless and never passes through availability, so checking only at 026 would leave 027 open.
/// </summary>
public static class PractitionerBranchRules
{
    public const string ProblemType = "urn:hbmp:practitioner-not-at-branch";

    /// <summary>Refuse only on a definite NO. Unknown proceeds — see
    /// <see cref="UnknownPractitionerBranchDirectory"/> for why an unavailable metadata service must not stop
    /// a clinic from booking patients.</summary>
    public static string? Refuse(bool? servesBranch, Guid practitionerId, Guid branchId) =>
        servesBranch is false
            ? $"Practitioner {practitionerId} has no active assignment to branch {branchId}. " +
              "Assign the practitioner to this branch first, or choose a doctor who works here."
            : null;

    /// <summary>
    /// 25.3 (design 42 §3) — refuse when the licence has EXPIRED as at the date being booked.
    ///
    /// Same shape and same fail-open reasoning as <see cref="Refuse"/> above, and for the same reason: a
    /// provider-service outage must not stop six clinics from booking patients. Unknown proceeds; only a
    /// definite "expired on this date" refuses.
    ///
    /// The two rules stay SEPARATE rather than merging into one "is bookable" boolean, because the remedies
    /// differ and the desk needs to know which it hit. "Not assigned to this branch" is fixed by assigning
    /// them or picking another doctor; "licence expired on 30 September" is fixed by recording a renewal or
    /// finding cover, and the date is the fact that decides which.
    /// </summary>
    public static string? RefuseExpiredLicence(
        bool? licenceValid, DateOnly? licenceExpiry, Guid practitionerId, DateOnly asOf) =>
        licenceValid is false
            ? licenceExpiry is { } expiry
                ? $"Practitioner {practitionerId} held no valid licence on {asOf:yyyy-MM-dd}: it expired on " +
                  $"{expiry:yyyy-MM-dd}. Record the renewal, or book a practitioner who is licensed on that date."
                : $"Practitioner {practitionerId} held no valid licence on {asOf:yyyy-MM-dd}."
            : null;

    /// <summary>RFC 7807 type for the licence refusal — distinct from
    /// <see cref="ProblemType"/> so a client can tell the two remedies apart.</summary>
    public const string LicenceExpiredProblemType = "urn:hbmp:practitioner-licence-expired";
}
