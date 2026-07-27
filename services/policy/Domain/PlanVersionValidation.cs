namespace Mersal.Policy.Domain;

/// <summary>One reason a draft version cannot be activated. Machine code + a human sentence, so the API can
/// return the whole set at once (an author fixing a plan should see every problem, not the first one).</summary>
public sealed record ActivationProblem(string Code, string Detail);

/// <summary>
/// Phase 19.1 — the gate a draft must pass before it becomes an in-force, immutable benefit configuration.
///
/// This is deliberately a PURE function over the draft: the database already refuses a structurally impossible
/// rule (limit type without value, both co-pay forms, a threshold without pre-auth, overlapping ranges), and
/// those CHECKs are the real guarantee. What the DB cannot express is *plan-level* sense — a version that
/// covers nothing, or one whose window has already been overtaken. Activation is the last moment either can be
/// corrected, because after it the version can never be edited again.
/// </summary>
public static class PlanVersionValidation
{
    /// <summary>Every reason <paramref name="version"/> may not be activated, empty when it may.</summary>
    public static IReadOnlyList<ActivationProblem> Validate(PlanVersion version, DateOnly today)
    {
        var problems = new List<ActivationProblem>();

        if (version.Status != PlanVersionStatus.Draft)
            problems.Add(new("NOT_DRAFT", $"Only a Draft version can be activated; this one is {version.Status}."));

        if (version.Rules.Count == 0)
            problems.Add(new("NO_RULES", "A plan version must configure at least one benefit category."));
        else if (!version.Rules.Any(r => r.IsCovered))
            problems.Add(new("NO_COVERED_CATEGORY", "A plan version must cover at least one benefit category."));

        if (version.EffectiveTo is not null && version.EffectiveTo.Value <= version.EffectiveFrom)
            problems.Add(new("BAD_WINDOW", "effectiveTo must be after effectiveFrom (the end is exclusive)."));

        // A version whose whole window is already in the past would activate straight into Superseded-by-time
        // and could never govern a future service date. Almost always an author's date slip.
        if (version.EffectiveTo is not null && version.EffectiveTo.Value <= today)
            problems.Add(new("WINDOW_ELAPSED", "The version's effective window has already elapsed."));

        foreach (var rule in version.Rules)
            problems.AddRange(ValidateRule(rule));

        return problems;
    }

    private static IEnumerable<ActivationProblem> ValidateRule(BenefitRule rule)
    {
        // A category that is not covered carries no entitlement, so any limit/co-pay on it is dead configuration
        // that would silently mislead whoever reads the plan.
        if (!rule.IsCovered && (rule.LimitValue is not null || rule.CopayFixed is not null || rule.CopayPercent is not null))
            yield return new("UNCOVERED_WITH_BENEFITS",
                $"Category {rule.BenefitCategoryId} is not covered but carries limits or co-pay.");

        // Covered with no limit is legitimate (unlimited); covered with a ZERO limit is not — it reads as
        // "covered" everywhere in the UI while entitling nothing.
        if (rule is { IsCovered: true, LimitValue: 0m })
            yield return new("ZERO_LIMIT",
                $"Category {rule.BenefitCategoryId} is covered with a zero limit; mark it not covered instead.");

        // A reset period only means something for a limit that accumulates over a window. Lifetime never resets,
        // and a rule with no limit has nothing to reset.
        if (rule.ResetPeriod != ResetPeriod.None && rule.LimitType is null)
            yield return new("RESET_WITHOUT_LIMIT",
                $"Category {rule.BenefitCategoryId} has a reset period but no limit.");
        if (rule.ResetPeriod != ResetPeriod.None && rule.LimitType == Domain.LimitType.Lifetime)
            yield return new("LIFETIME_RESET",
                $"Category {rule.BenefitCategoryId} is a Lifetime limit and cannot reset.");

        // A pre-auth threshold above the benefit's own ceiling can never be reached — the rule would never fire.
        if (rule is { RequiresPreauth: true, PreauthCostThreshold: not null, LimitValue: not null }
            && rule.PreauthCostThreshold.Value > rule.LimitValue.Value)
            yield return new("THRESHOLD_ABOVE_LIMIT",
                $"Category {rule.BenefitCategoryId} has a pre-auth threshold above its own limit; it can never trigger.");
    }
}
