namespace Mersal.Money;

/// <summary>
/// The cost-share terms that apply at one network tier, as a plain value — no entity, no service, no I/O.
///
/// policy-service authors these (policy.benefit_rule_tier); eligibility previews them and claims adjudicates
/// with them. They travel as a value precisely so all three compute the member's share the SAME way: the
/// eligibility card a receptionist reads out and the amount the claim finally charges must agree, and the
/// surest way to make them disagree is to implement the arithmetic twice.
/// </summary>
/// <param name="IsCovered">False = not covered at this tier. The member pays the whole allowed amount.</param>
/// <param name="CopayFixed">A flat amount per service. Mutually exclusive with <paramref name="CopayPercent"/>.</param>
/// <param name="CopayPercent">A percentage of the amount remaining after the deductible. 0–100.</param>
/// <param name="CoinsurancePercent">A percentage of what remains after deductible and co-pay. 0–100.</param>
/// <param name="Deductible">Amount the member pays before the benefit contributes anything.</param>
public readonly record struct TierCostShareTerms(
    bool IsCovered,
    decimal? CopayFixed = null,
    decimal? CopayPercent = null,
    decimal? CoinsurancePercent = null,
    decimal? Deductible = null);

/// <summary>The split of one allowed amount, with the components kept separate so a member can be told WHY
/// they owe what they owe rather than just how much.</summary>
public readonly record struct CostShareSplit(
    Money AllowedAmount,
    Money DeductibleApplied,
    Money Copay,
    Money Coinsurance,
    Money MemberShare,
    Money PayerShare);

/// <summary>
/// Phase 19.1b — how an allowed amount splits between member and payer at a given network tier.
///
/// <para><b>The order of operations is a benefit-policy decision, not an arithmetic detail</b>, and it changes
/// what real people pay. This implements: <b>deductible first, then co-pay, then coinsurance on what is left.</b>
/// Applying coinsurance before the deductible instead would charge the member a percentage of money they are
/// already paying in full — double-counting — so this order is the defensible one. It is recorded in ADR-0019
/// and needs sponsor sign-off before go-live, exactly like the plan-change carry-forward rule.</para>
///
/// <para>Two properties hold for every input: the member never pays more than the allowed amount, and member +
/// payer always equals it exactly. Both are asserted rather than assumed — a split that does not reconcile is
/// how a settlement batch stops matching the sum of its lines.</para>
/// </summary>
public static class CostShareCalculator
{
    /// <summary>Split <paramref name="allowed"/> at the given tier terms.</summary>
    public static CostShareSplit Split(Money allowed, TierCostShareTerms terms)
    {
        var zero = new Money(0m, allowed.Currency);
        if (allowed.Amount <= 0m)
            return new CostShareSplit(allowed, zero, zero, zero, zero, zero);

        // Not covered at this tier is not a co-pay of 100% — it is the absence of a benefit. The member owes
        // the whole amount and none of the component fields carry a misleading breakdown.
        if (!terms.IsCovered)
            return new CostShareSplit(allowed, zero, zero, zero, allowed, zero);

        var remaining = allowed.Amount;

        var deductible = Clamp(terms.Deductible ?? 0m, remaining);
        remaining -= deductible;

        // Fixed and percentage co-pay are alternatives (the database rejects both together). Fixed wins here
        // only so the function is total: a row carrying both never reaches this code.
        var copay = terms.CopayFixed is { } fixedCopay
            ? Clamp(fixedCopay, remaining)
            : Clamp(Percent(remaining, terms.CopayPercent), remaining);
        remaining -= copay;

        var coinsurance = Clamp(Percent(remaining, terms.CoinsurancePercent), remaining);
        remaining -= coinsurance;

        var member = new Money(deductible + copay + coinsurance, allowed.Currency);
        // Payer share is the residue rather than a second computation, so the two always sum to the allowed
        // amount even where rounding would otherwise leave a stray piastre unaccounted for.
        var payer = new Money(allowed.Amount - member.Amount, allowed.Currency);

        return new CostShareSplit(
            allowed,
            new Money(deductible, allowed.Currency),
            new Money(copay, allowed.Currency),
            new Money(coinsurance, allowed.Currency),
            member,
            payer);
    }

    /// <summary>The member's share alone — the single number claims adjudication needs.</summary>
    public static Money MemberShare(Money allowed, TierCostShareTerms terms) => Split(allowed, terms).MemberShare;

    private static decimal Percent(decimal amount, decimal? percent) =>
        percent is { } p && p > 0m
            ? decimal.Round(amount * p / 100m, Money.Scale, MidpointRounding.ToEven)
            : 0m;

    /// <summary>Never negative, never more than what is left. A co-pay larger than the bill is a configuration
    /// error, and charging it would make the member pay more than the service cost.</summary>
    private static decimal Clamp(decimal value, decimal ceiling) => Math.Max(0m, Math.Min(value, ceiling));
}
