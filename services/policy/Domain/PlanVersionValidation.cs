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
    /// <param name="activeTiers">The Active network tiers (19.1b). EVERY one must be priced on every covered
    /// category — an unconfigured tier is a validation error, not a silent default, because the alternative is
    /// adjudicating a real claim against a cost share nobody ever agreed.</param>
    public static IReadOnlyList<ActivationProblem> Validate(
        PlanVersion version, DateOnly today, IReadOnlyCollection<NetworkTierRef> activeTiers)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(activeTiers);
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
            problems.AddRange(ValidateRule(rule, activeTiers));

        return problems;
    }

    private static IEnumerable<ActivationProblem> ValidateRule(
        BenefitRule rule, IReadOnlyCollection<NetworkTierRef> activeTiers)
    {
        // A category that is not covered carries no entitlement, so any limit on it is dead configuration that
        // would silently mislead whoever reads the plan.
        if (!rule.IsCovered && rule.LimitValue is not null)
            yield return new("UNCOVERED_WITH_BENEFITS",
                $"Category {rule.BenefitCategoryId} is not covered but carries a limit.");

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

        foreach (var problem in ValidateTiers(rule, activeTiers))
            yield return problem;
    }

    /// <summary>
    /// 19.1b — the cost-share grid must be COMPLETE (design 38 §4.1b).
    ///
    /// A covered category with no row for an Active tier is the dangerous case, because nothing about it looks
    /// wrong: the plan reads as covered, the tier exists, and adjudication reaches a service delivered there
    /// with no agreed member share. Whatever it then does — charge nothing, charge everything, fall through to
    /// a default — is a number no-one authored. Activation is the last moment this is fixable, so an absent
    /// tier is an error and "not covered at this tier" must be stated explicitly instead.
    /// </summary>
    private static IEnumerable<ActivationProblem> ValidateTiers(
        BenefitRule rule, IReadOnlyCollection<NetworkTierRef> activeTiers)
    {
        var configured = rule.Tiers.Select(t => t.NetworkTierId).ToHashSet();

        if (rule.IsCovered)
        {
            foreach (var tier in activeTiers.Where(t => !configured.Contains(t.NetworkTierId)))
                yield return new("TIER_NOT_CONFIGURED",
                    $"Category {rule.BenefitCategoryId} has no cost share for tier {tier.TierCode}; " +
                    "state it explicitly, including 'not covered at this tier'.");
        }
        else if (rule.Tiers.Count > 0)
        {
            // Cost share under a category that is not covered at all is dead configuration in the same way a
            // limit is — it renders as an entitlement in every UI that reads the grid.
            yield return new("UNCOVERED_WITH_TIER_COST_SHARE",
                $"Category {rule.BenefitCategoryId} is not covered but carries a per-tier cost share.");
        }

        var activeIds = activeTiers.Select(t => t.NetworkTierId).ToHashSet();
        foreach (var stale in rule.Tiers.Where(t => !activeIds.Contains(t.NetworkTierId)))
            yield return new("UNKNOWN_TIER",
                $"Category {rule.BenefitCategoryId} prices tier {stale.TierCode}, which is not an Active network tier.");

        foreach (var duplicate in rule.Tiers.GroupBy(t => t.NetworkTierId).Where(g => g.Count() > 1))
            yield return new("DUPLICATE_TIER",
                $"Category {rule.BenefitCategoryId} prices tier {duplicate.First().TierCode} more than once.");

        foreach (var tier in rule.Tiers)
        {
            // The database rejects both co-pay forms together; saying so here means an author sees it in the
            // same list as everything else rather than as a lone 409 after fixing the rest.
            if (tier is { CopayFixed: not null, CopayPercent: not null })
                yield return new("BOTH_COPAY_FORMS",
                    $"Tier {tier.TierCode} on category {rule.BenefitCategoryId} sets both a fixed and a percentage co-pay.");

            if (tier is { IsCovered: true, LimitMultiplier: 0m })
                yield return new("ZERO_TIER_MULTIPLIER",
                    $"Tier {tier.TierCode} on category {rule.BenefitCategoryId} is covered with a zero limit multiplier; " +
                    "mark it not covered at this tier instead.");
        }
    }
}
