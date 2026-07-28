namespace Mersal.BenefitPricing;

// Phase 19.6b — the utilization vocabulary, moved here from services/policy/Domain/QueryModel.cs.
//
// ============================================================================================================
// WHY IT MOVED
// ============================================================================================================
// QueryModel's own comment stated the rule: "If each of the three defined its own utilization bands, a member
// could sit in High on the dashboard, Medium in an extract and neither in a query, and every one of those
// screens would look correct on its own." It then put the bands in POLICY'S domain — which works for policy
// query and the extract engine, because both live in policy-service, and does not work for the dashboard,
// because reporting-service is a different service and may not reference another service's domain.
//
// So the definition is here, in a library five services already depend on for benefit maths. Policy re-exports
// it (see QueryModel.cs) so nothing that referenced `Mersal.Policy.Domain.UtilizationBand` had to change, and
// the dashboard classifies members with the same code the query does rather than a copy that agrees today.

/// <summary>How much of a member's (or a policy's) accumulating entitlement is gone. Bands rather than a raw
/// percentage because the question they answer is triage — "who do I look at first" — and a band survives the
/// rounding argument that a percentage invites.</summary>
public enum UtilizationBand
{
    /// <summary>Nothing consumed. Worth its own band: a member who has used NOTHING all year is either healthy,
    /// unaware of their entitlement, or wrongly enrolled — and the third case is only findable this way.</summary>
    Zero,
    /// <summary>Above zero, below 50%.</summary>
    Low,
    /// <summary>50% up to (not including) 80%.</summary>
    Medium,
    /// <summary>80% up to (not including) 100% — the threshold-crossing band.</summary>
    High,
    /// <summary>At or over the limit. Over 100% is possible and legitimate (a limit reduced mid-period), and it
    /// stays in this band rather than being clamped away.</summary>
    Exhausted,
    /// <summary>Covered with no accumulating ceiling. NOT the same as Zero, and the distinction matters: an
    /// unlimited benefit reported as 0% invites "plenty left" on something that was never metered.</summary>
    Unlimited,
}

public static class UtilizationBands
{
    /// <summary>Classify from the accumulator totals. <paramref name="hasCoverage"/> separates "unlimited" from
    /// "not covered at all", which are otherwise identical (both sum to a zero limit).</summary>
    public static UtilizationBand Of(decimal limit, decimal consumed, bool hasCoverage)
    {
        if (limit <= 0m) return hasCoverage ? UtilizationBand.Unlimited : UtilizationBand.Zero;
        if (consumed <= 0m) return UtilizationBand.Zero;
        if (consumed >= limit) return UtilizationBand.Exhausted;

        var percent = consumed / limit * 100m;
        return percent >= 80m ? UtilizationBand.High
             : percent >= 50m ? UtilizationBand.Medium
             : UtilizationBand.Low;
    }

    /// <summary>The percentage a band is rendered with; null for Unlimited (see <see cref="UtilizationBand"/>).</summary>
    public static decimal? PercentUsed(decimal limit, decimal consumed) =>
        limit <= 0m ? null : Math.Round(consumed / limit * 100m, 1, MidpointRounding.AwayFromZero);

    public static bool TryParse(string? raw, out UtilizationBand band) =>
        Enum.TryParse(raw, ignoreCase: true, out band);
}
