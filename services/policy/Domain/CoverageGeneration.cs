using System.Text.Json;

namespace Mersal.Policy.Domain;

/// <summary>
/// Phase 19.2 — a member's coverage is GENERATED from a plan version, not hand-entered (design 38 §7.3).
///
/// This is the join between the product layer (19.1) and the benefit spine that eligibility and the phase-18
/// accumulator already run on. The output must stay shape-compatible with <c>EligibilityEngine</c>: one
/// <c>Coverage</c> per covered benefit category, each with zero-or-more <c>CoverageLimit</c> rows carrying
/// limit type, value, reset period and a <c>ConsumedValue</c> that starts at zero.
///
/// <para><b>This module never writes a non-zero <c>consumed_value</c>.</b> Phase 18 owns that accumulator and
/// is its only writer. Generation initializes it to zero on a NEW coverage row; carrying usage forward across
/// a plan change is done by <see cref="ConsumptionCarryForward"/>, which computes what the new limit should
/// be — it does not move the accumulator either.</para>
/// </summary>
public static class CoverageGenerator
{
    /// <summary>
    /// The coverage rows a plan version's benefit rules produce for one enrolment.
    /// </summary>
    /// <param name="version">The plan version in force for this member's elected plan.</param>
    /// <param name="enrollment">Supplies the beneficiary, the policy, and the coverage window.</param>
    /// <param name="tenantId">Stamped on every generated row (RLS).</param>
    /// <returns>One coverage per COVERED category. Uncovered categories produce nothing: a coverage row that
    /// exists but grants nothing reads as an entitlement everywhere it is rendered.</returns>
    public static IReadOnlyList<Coverage> Generate(PlanVersion version, Enrollment enrollment, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(enrollment);

        var generated = new List<Coverage>();
        foreach (var rule in version.Rules.Where(r => r.IsCovered))
        {
            var coverage = new Coverage
            {
                CoverageId = Guid.NewGuid(),
                TenantId = tenantId,
                PolicyId = enrollment.PolicyId,
                BeneficiaryId = enrollment.BeneficiaryId,
                BenefitCategoryId = rule.BenefitCategoryId,
                // The coverage window IS the enrolment window. Deriving it from the plan version instead would
                // cover a member for days they were not enrolled, and vice versa.
                EffectiveFrom = enrollment.EffectiveFrom,
                EffectiveTo = enrollment.EffectiveTo,
                Status = CoverageStatus.Active,
            };

            // A rule with no limit is unlimited within the category — legitimate, and represented by generating
            // no limit row at all rather than a sentinel value that arithmetic would then have to special-case.
            if (rule is { LimitType: not null, LimitValue: not null })
            {
                coverage.Limits.Add(new CoverageLimit
                {
                    CoverageLimitId = Guid.NewGuid(),
                    TenantId = tenantId,
                    CoverageId = coverage.CoverageId,
                    LimitType = rule.LimitType.Value,
                    LimitValue = rule.LimitValue.Value,
                    ConsumedValue = 0m,          // phase 18 owns every subsequent change to this
                    ResetPeriod = rule.ResetPeriod,
                });
            }

            generated.Add(coverage);
        }
        return generated;
    }
}

/// <summary>
/// Phase 19.2 — when a newly enrolled member's benefit actually becomes payable.
///
/// A waiting period is counted from the enrolment's effective date, per benefit category, and the value stored
/// is the LAST day still inside it — so a service on that date is not payable and the next day is. Storing the
/// boundary rather than recomputing it everywhere means eligibility, claims and the member's own card cannot
/// disagree about which day cover starts.
/// </summary>
public static class WaitingPeriod
{
    /// <summary>
    /// The last day inside the waiting period for this enrolment, or null when no category imposes one.
    ///
    /// <para>The LONGEST waiting period across covered categories wins as the enrolment-level value, because
    /// that is the date after which the member's whole package is live. Per-category dates are resolved from
    /// the benefit rules at check time; this is the single summary date the member is told.</para>
    /// </summary>
    public static DateOnly? EndsOn(PlanVersion version, DateOnly effectiveFrom)
    {
        ArgumentNullException.ThrowIfNull(version);
        var longest = version.Rules.Where(r => r.IsCovered).Select(r => r.WaitingPeriodDays).DefaultIfEmpty(0).Max();
        // Zero days means cover starts on the effective date itself — no waiting period, and no stored boundary
        // that a reader could mistake for "one day of waiting".
        return longest <= 0 ? null : effectiveFrom.AddDays(longest - 1);
    }

    /// <summary>The waiting-period boundary for ONE benefit category, used when adjudicating a specific service.</summary>
    public static DateOnly? EndsOnFor(BenefitRule rule, DateOnly effectiveFrom)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.WaitingPeriodDays <= 0 ? null : effectiveFrom.AddDays(rule.WaitingPeriodDays - 1);
    }
}

/// <summary>Declarative criteria for electing a member onto a plan (19.2b). Deserialized from the
/// <c>policy_plan.eligibility_rule</c> jsonb, so a restriction is data an administrator can read and change
/// rather than a branch in code they cannot see.</summary>
public sealed record PlanEligibilityRule(
    Guid[]? GroupIds = null,
    string[]? Relationships = null,
    int? MinAge = null,
    int? MaxAge = null,
    Guid[]? BranchIds = null);

/// <summary>The facts about a candidate member that a rule is evaluated against.</summary>
public sealed record ElectionCandidate(
    Guid? GroupId, Relationship Relationship, int? AgeYears, Guid? BranchId);

/// <summary>One criterion a candidate failed, named so the 422 can say which — "not eligible" alone sends an
/// officer hunting through a plan definition they may not be able to see.</summary>
public sealed record ElectionFailure(string Criterion, string Detail);

/// <summary>
/// Phase 19.2b — may this candidate be elected onto this plan?
///
/// Pure, and deliberately CONJUNCTIVE: every criterion present must pass. A rule that named a group and a
/// relationship and accepted either would let a member onto a restricted plan by satisfying the looser half,
/// which is the opposite of what a restriction is for.
/// </summary>
public static class PlanEligibility
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parse a plan's stored rule. A null or empty rule means "no restriction", which is different
    /// from a rule that restricts to nothing — an empty criteria array below excludes everyone.</summary>
    public static PlanEligibilityRule? Parse(string? eligibilityRuleJson)
    {
        if (string.IsNullOrWhiteSpace(eligibilityRuleJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<PlanEligibilityRule>(eligibilityRuleJson, Json);
        }
        catch (JsonException)
        {
            // Unreadable configuration is NOT "no restriction". Treating a malformed rule as open would let a
            // typo silently unlock a restricted plan, so the caller is told the rule could not be read.
            return null;
        }
    }

    /// <summary>Whether the stored rule is present but unparseable — the case the caller must refuse rather
    /// than treat as unrestricted.</summary>
    public static bool IsMalformed(string? eligibilityRuleJson) =>
        !string.IsNullOrWhiteSpace(eligibilityRuleJson) && Parse(eligibilityRuleJson) is null;

    /// <summary>Every criterion the candidate fails, empty when they may be elected.</summary>
    public static IReadOnlyList<ElectionFailure> Evaluate(PlanEligibilityRule? rule, ElectionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (rule is null) return [];

        var failures = new List<ElectionFailure>();

        if (rule.GroupIds is { } groups && (candidate.GroupId is not { } g || !groups.Contains(g)))
            failures.Add(new("groupIds", candidate.GroupId is null
                ? "This plan is restricted to specific member groups and the member is not in a group."
                : $"The member's group {candidate.GroupId} is not among the groups this plan admits."));

        if (rule.Relationships is { } relationships
            && !relationships.Contains(candidate.Relationship.ToString(), StringComparer.OrdinalIgnoreCase))
            failures.Add(new("relationships",
                $"This plan admits {string.Join(", ", relationships)}; the member is a {candidate.Relationship}."));

        if (rule.MinAge is { } min)
        {
            if (candidate.AgeYears is not { } age)
                failures.Add(new("minAge", "This plan has an age floor and the member's age is unknown."));
            else if (age < min)
                failures.Add(new("minAge", $"This plan admits members aged {min} and over; the member is {age}."));
        }

        if (rule.MaxAge is { } max)
        {
            if (candidate.AgeYears is not { } age)
                failures.Add(new("maxAge", "This plan has an age ceiling and the member's age is unknown."));
            else if (age > max)
                failures.Add(new("maxAge", $"This plan admits members up to {max}; the member is {age}."));
        }

        if (rule.BranchIds is { } branches && (candidate.BranchId is not { } b || !branches.Contains(b)))
            failures.Add(new("branchIds", "This plan is restricted to specific branches."));

        return failures;
    }
}
