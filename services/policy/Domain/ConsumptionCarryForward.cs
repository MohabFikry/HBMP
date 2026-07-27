namespace Mersal.Policy.Domain;

/// <summary>
/// How a member's already-used benefit is treated when they move between plans (19.2b).
///
/// <b>This is a benefit-policy decision with two defensible answers, and it is NOT settled.</b> ADR-0020 is
/// Proposed, awaiting Medical Director and Finance signatures. It is a setting rather than a hard-coded rule
/// precisely because reversing it later must not require migrating every member's accumulator.
/// </summary>
public enum PlanChangeConsumptionPolicy
{
    /// <summary>
    /// ADR-0020's proposal, and the default. The year's usage belongs to the MEMBER, independent of which plan
    /// they sat in: remaining at the new plan = new limit − already consumed, floored at zero.
    ///
    /// <para>Without this, a plan change becomes a way to obtain a fresh benefit ceiling mid-year — a cost
    /// exposure, and more importantly an unfairness between members who happened to be moved and members who
    /// were not.</para>
    /// </summary>
    CarryForward,

    /// <summary>
    /// Each plan is its own contract with its own ceiling. Arguably the correct reading when the two plans are
    /// funded by DIFFERENT PAYERS — under carry-forward a member moving from a donor-funded plan to a
    /// government-funded one makes the government payer inherit the donor's spend against its own ceiling,
    /// which may be exactly wrong. The open question in ADR-0020.
    /// </summary>
    ResetPerPlan,
}

/// <summary>What a member has already used in one benefit category, and what the new plan offers there.</summary>
/// <param name="BenefitCategoryId">The category being carried across.</param>
/// <param name="ConsumedValue">Read from the phase-18 accumulator. NEVER written by this module.</param>
/// <param name="NewLimitValue">The new plan version's limit for the category; null = unlimited.</param>
public readonly record struct CategoryCarryForward(
    Guid BenefitCategoryId, decimal ConsumedValue, decimal? NewLimitValue);

/// <summary>The limit and remaining balance a category should show after the move.</summary>
public readonly record struct CarriedLimit(
    Guid BenefitCategoryId, decimal? LimitValue, decimal ConsumedValue, decimal? Remaining, bool Exhausted);

/// <summary>
/// Phase 19.2b — the arithmetic of a plan change, as a pure function.
///
/// <para><b>The accumulator is never written here.</b> Phase 18 owns <c>consumed_value</c> and is its only
/// writer; this computes what the new coverage's LIMIT should be and what the member has left, reading
/// consumption and leaving it exactly where it was. Re-introducing a second writer is the X1 bug class the
/// whole benefit spine was rebuilt to close.</para>
/// </summary>
public static class ConsumptionCarryForward
{
    /// <summary>Apply the configured policy to one category.</summary>
    public static CarriedLimit Apply(CategoryCarryForward input, PlanChangeConsumptionPolicy policy)
    {
        // An unlimited category has nothing to exhaust, whichever policy is in force.
        if (input.NewLimitValue is not { } newLimit)
            return new CarriedLimit(input.BenefitCategoryId, null, CarriedConsumption(input, policy), null, false);

        var consumed = CarriedConsumption(input, policy);
        // Floored at zero: a member who used 800 under a 1,000 plan and moves to a 500 plan has nothing left,
        // not minus 300. A negative remaining would propagate into every display and comparison downstream.
        var remaining = Math.Max(0m, newLimit - consumed);
        return new CarriedLimit(input.BenefitCategoryId, newLimit, consumed, remaining, remaining <= 0m);
    }

    /// <summary>The whole set, in input order.</summary>
    public static IReadOnlyList<CarriedLimit> Apply(
        IEnumerable<CategoryCarryForward> inputs, PlanChangeConsumptionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return [.. inputs.Select(i => Apply(i, policy))];
    }

    private static decimal CarriedConsumption(CategoryCarryForward input, PlanChangeConsumptionPolicy policy) =>
        policy == PlanChangeConsumptionPolicy.CarryForward ? input.ConsumedValue : 0m;
}
