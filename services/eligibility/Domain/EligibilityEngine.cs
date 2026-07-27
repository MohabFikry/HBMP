namespace Mersal.Eligibility.Domain;

// Eligibility decision engine (15-database-erd §5, 17-api-specifications §5, 23-state-machines §1).
// Pure, side-effect-free rules — the authoritative decision surface. Snapshots/cache are derived and
// never authoritative; policy-service coverage_limit.consumed_value remains the source of truth.

/// <summary>Member lifecycle status as projected from patient-service events (23-state-machines §1).</summary>
public enum MemberStatus { Pending, Active, Suspended, Expired, Blocked, Inactive }

/// <summary>The decision domain is EXACTLY these three values (17-api-specifications §5).</summary>
public enum EligibilityDecision { Eligible, Ineligible, NeedsAuthorization }

/// <summary>Limit accumulator kind (mirrors policy-service LimitType).</summary>
public enum LimitType { Annual, PerEncounter, Lifetime, Count }

/// <summary>A single benefit limit and its consumption. Remaining is derived, never stored.</summary>
public sealed record LimitState(LimitType LimitType, decimal LimitValue, decimal ConsumedValue)
{
    public decimal Remaining => LimitValue - ConsumedValue;
}

/// <summary>A coverage row (projected from policy-service) applicable to a benefit category.</summary>
/// <param name="WaitingPeriodEndsOn">19.2 — the LAST day still inside the member's waiting period, or null
/// when none applies. A newly enrolled member holds valid coverage with remaining limits and is still not
/// payable until this date passes; without it here, the engine would return Eligible for exactly the window
/// the waiting period exists to exclude. Defaulted so existing callers are unaffected.</param>
public sealed record CoverageView(
    Guid CoverageId,
    string BenefitCategory,
    bool CoverageActive,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    IReadOnlyList<LimitState> Limits,
    DateOnly? WaitingPeriodEndsOn = null);

/// <summary>The three inputs to a decision: member status, coverage validity, remaining limits.</summary>
public sealed record EligibilityRequest(
    MemberStatus MemberStatus,
    string BenefitCategory,
    string? ServiceCode,
    bool ServiceRequiresPreAuth,
    IReadOnlyList<CoverageView> Coverages,
    DateOnly OnDate);

/// <summary>The computed decision + the reasons and the binding limit state (if any).</summary>
public sealed record EligibilityResult(
    EligibilityDecision Decision,
    Guid? CoverageId,
    IReadOnlyList<string> Reasons,
    LimitState? LimitState);

public static class EligibilityEngine
{
    /// <summary>
    /// Compute the decision from the THREE inputs. Only an Active member with a valid, in-effect
    /// coverage and remaining &gt; 0 is Eligible. A bad member status or no coverage is a hard
    /// Ineligible; an exhausted limit or a gated/pre-auth service is NeedsAuthorization (a soft No that
    /// routes to the approval team, not a denial).
    /// </summary>
    public static EligibilityResult Evaluate(EligibilityRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        // (1) Member status — only Active can ever be Eligible.
        if (req.MemberStatus != MemberStatus.Active)
            return new EligibilityResult(EligibilityDecision.Ineligible, null,
                [$"member status is {req.MemberStatus}"], null);

        // (2) Coverage validity — an active coverage for the category, in effect on the requested date.
        var coverage = req.Coverages.FirstOrDefault(c =>
            string.Equals(c.BenefitCategory, req.BenefitCategory, StringComparison.OrdinalIgnoreCase)
            && c.CoverageActive
            && c.EffectiveFrom <= req.OnDate
            && (c.EffectiveTo is null || c.EffectiveTo >= req.OnDate));

        if (coverage is null)
            return new EligibilityResult(EligibilityDecision.Ineligible, null,
                [$"no active coverage for {req.BenefitCategory}"], null);

        // (2b) Waiting period (19.2). The member IS covered — the policy is valid, the category matches and
        // the limits are intact — but the benefit is not yet payable. That is a hard Ineligible rather than a
        // NeedsAuthorization: no approval can shorten a waiting period, so routing it to the approvals team
        // would queue work nobody can action and tell the beneficiary to wait twice.
        if (coverage.WaitingPeriodEndsOn is { } waitingEnds && req.OnDate <= waitingEnds)
            return new EligibilityResult(EligibilityDecision.Ineligible, coverage.CoverageId,
                [$"WAITING_PERIOD: cover for {req.BenefitCategory} begins on {waitingEnds.AddDays(1):yyyy-MM-dd}"],
                null);

        // (3) Remaining limits — the binding limit is the one with the least remaining.
        var binding = coverage.Limits.Count == 0
            ? null
            : coverage.Limits.OrderBy(l => l.Remaining).First();

        var reasons = new List<string>();
        if (req.ServiceRequiresPreAuth)
            reasons.Add(req.ServiceCode is null
                ? "service requires pre-authorization"
                : $"service {req.ServiceCode} requires pre-authorization");
        if (binding is not null && binding.Remaining <= 0)
            reasons.Add($"{binding.LimitType} limit reached (remaining {binding.Remaining:0.###})");

        // (4) Gated / exhausted → NeedsAuthorization (soft No, routes to approvals).
        if (reasons.Count > 0)
            return new EligibilityResult(EligibilityDecision.NeedsAuthorization, coverage.CoverageId, reasons, binding);

        // (5) Eligible.
        return new EligibilityResult(EligibilityDecision.Eligible, coverage.CoverageId,
            [$"active coverage for {req.BenefitCategory}"], binding);
    }
}
