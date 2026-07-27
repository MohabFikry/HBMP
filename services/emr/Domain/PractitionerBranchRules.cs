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
}
