namespace Mersal.Claims.Domain;

/// <summary>The match key for a provider-submitted line (36 §3.2, §5 check 4): a delivered/authorized service is
/// identified by <c>(provider, beneficiary, service code, authorization)</c>; the service DATE is compared separately
/// with a tolerance window because an invoice date and a fulfillment date rarely land on the same calendar day.</summary>
public readonly record struct MatchKey(
    Guid ProviderId, Guid BeneficiaryId, ClaimCodeSystem CodeSystem, string Code, Guid? AuthorizationId);

/// <summary>The three outcomes of comparing a submitted line to a candidate fulfillment.</summary>
public enum MatchDecision { Match, NearMissDate, NoMatch }

/// <summary>Pure matching logic for provider-submitted claim lines (10b.5). Kept free of infrastructure so the
/// key + date-tolerance rules are unit-tested in isolation. Matching NEVER auto-approves — it only decides which
/// existing fulfillment (if any) a submitted line corresponds to; the payable outcome is set downstream.</summary>
public static class SubmissionMatcher
{
    /// <summary>Default service-date tolerance: an invoice may be dated up to two days off the fulfillment date.
    /// Documented and configurable (Claims:MatchToleranceDays); widen with care — it trades false-misses for
    /// false-matches, and a false match records the wrong billed/contract pairing.</summary>
    public const int DefaultToleranceDays = 2;

    /// <summary>True when two service dates are within <paramref name="toleranceDays"/> calendar days of each other.</summary>
    public static bool WithinTolerance(DateOnly submitted, DateOnly candidate, int toleranceDays) =>
        Math.Abs(candidate.DayNumber - submitted.DayNumber) <= Math.Max(0, toleranceDays);

    /// <summary>Decide whether a submitted line matches a candidate fulfillment: the keys must be equal AND the dates
    /// within tolerance. Equal keys but an out-of-window date is a NEAR-MISS (surfaced for manual assessment, never
    /// auto-matched); a different key is NO-MATCH.</summary>
    public static MatchDecision Decide(
        MatchKey submitted, DateOnly submittedDate, MatchKey candidate, DateOnly candidateDate, int toleranceDays)
    {
        if (!submitted.Equals(candidate)) return MatchDecision.NoMatch;
        return WithinTolerance(submittedDate, candidateDate, toleranceDays)
            ? MatchDecision.Match : MatchDecision.NearMissDate;
    }
}
