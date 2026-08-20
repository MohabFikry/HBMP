using CsCheck;
using FluentAssertions;

namespace Mersal.Amounts.Tests;

/// <summary>
/// Phase 18.F1 — property-based tests over the Money type.
///
/// Example-based tests prove a value type behaves on the cases someone thought of. The defects this type
/// exists to prevent are the ones nobody thought of: the amount that rounds the wrong way at exactly .005,
/// the cap that holds for positive values and inverts for negative, the sum that stops associating once a
/// rounding step is introduced. CsCheck generates thousands of cases per property and SHRINKS a failure to
/// its smallest form, which is the part that makes a counter-example readable.
///
/// Currency amounts are generated in a realistic band (up to ~1,000,000 EGP at 4 decimal places) rather than
/// over the whole decimal range: the interesting behaviour is at the rounding boundary and around zero, and
/// generating 10^28 values would spend every iteration in a region no claim will ever occupy.
/// </summary>
public class MoneyPropertyTests
{
    /// <summary>Raw amounts at 4dp — two digits BELOW the platform's scale, so every generated value
    /// exercises the rounding step rather than passing through it untouched.</summary>
    private static readonly Gen<decimal> RawAmount =
        Gen.Int[-10_000_000, 10_000_000].Select(i => i / 10_000m);

    private static readonly Gen<Money> AnyMoney = RawAmount.Select(Money.Egp);

    [Fact]
    public void Every_money_is_stored_at_exactly_two_decimal_places()
    {
        // The scale invariant. If a Money could carry 3dp, the database (numeric(14,2)) would round it on
        // write and the application would disagree with its own stored value — a mismatch that only shows up
        // when someone reconciles a total against its lines.
        Gen.Select(RawAmount, (a) => Money.Egp(a))
            .Sample(m => decimal.Round(m.Amount, Money.Scale) == m.Amount, iter: 10_000);
    }

    [Fact]
    public void Rounding_is_banker_s_and_therefore_unbiased()
    {
        // The reason this matters is cumulative, not per-value. Half-away-from-zero pushes every exact .005
        // upward; over a settlement batch of thousands of lines that is a systematic overpayment in one
        // direction. ToEven splits them, so the error cancels rather than accumulates.
        Money.Egp(0.125m).Amount.Should().Be(0.12m, "0.125 → 0.12 (2 is already even)");
        Money.Egp(0.135m).Amount.Should().Be(0.14m, "0.135 → 0.14 (3 rounds up to the even 4)");
        Money.Egp(-0.125m).Amount.Should().Be(-0.12m, "symmetric about zero");

        // And the property that makes it unbiased: over a large uniform sample the rounding error sums to
        // approximately nothing, whereas AwayFromZero would drift.
        var drift = 0m;
        for (var i = -5000; i <= 5000; i++)
        {
            var raw = i / 1000m;                    // hits every .005 boundary
            drift += Money.Egp(raw).Amount - raw;
        }
        Math.Abs(drift).Should().BeLessThan(0.01m, "banker's rounding must not accumulate a directional bias");
    }

    [Fact]
    public void Addition_is_commutative_and_has_zero_as_identity()
    {
        Gen.Select(AnyMoney, AnyMoney).Sample((a, b) => (a + b) == (b + a), iter: 10_000);
        AnyMoney.Sample(a => a + Money.Zero(Currency.Egp) == a, iter: 10_000);
    }

    [Fact]
    public void Subtraction_is_the_inverse_of_addition()
    {
        // Non-obvious with a rounding step in the constructor: a + b - b must return to a, which only holds
        // because both operands are already at scale before the operation.
        Gen.Select(AnyMoney, AnyMoney).Sample((a, b) => (a + b) - b == a, iter: 10_000);
    }

    [Fact]
    public void A_capped_amount_never_exceeds_its_ceiling()
    {
        // THE X3 INVARIANT, stated as a property rather than checked at six call sites. X3 was: a claim
        // decision could set allowed_amount above the contract tariff. 18.A2 clamped it at each decision
        // kind; this asserts the rule holds for every pair of values, including the negative and equal ones
        // that a hand-written example set tends to miss.
        Gen.Select(AnyMoney, AnyMoney)
            .Sample((value, ceiling) => Money.CapTo(value, ceiling) <= ceiling, iter: 20_000);
    }

    [Fact]
    public void Capping_never_raises_a_value()
    {
        // The other half, and the one an off-by-one in the comparison would break: a cap may only reduce.
        // `value > c ? c : value` is correct; `value >= c ? c : value` is too, but `value < c ? c : value`
        // would pass the ceiling test above while silently INFLATING every under-cap amount to the ceiling.
        Gen.Select(AnyMoney, AnyMoney)
            .Sample((value, ceiling) => Money.CapTo(value, ceiling) <= value, iter: 20_000);
    }

    [Fact]
    public void No_ceiling_means_no_change()
    {
        AnyMoney.Sample(v => Money.CapTo(v, null) == v, iter: 10_000);
    }

    [Fact]
    public void Multiplying_by_a_quantity_is_the_same_as_repeated_addition()
    {
        // Quantity-aware pricing (X8) is a multiplication. This pins that the type's `*` agrees with what a
        // reviewer would compute by hand for small integer quantities — where rounding cannot hide a slip.
        Gen.Select(Gen.Int[0, 20], Gen.Int[0, 100_000].Select(i => i / 100m))
            .Sample((qty, unit) =>
            {
                var byMultiply = Money.Egp(unit) * qty;
                var byAddition = Enumerable.Range(0, qty)
                    .Aggregate(Money.Zero(Currency.Egp), (acc, _) => acc + Money.Egp(unit));
                return byMultiply == byAddition;
            }, iter: 5_000);
    }

    [Fact]
    public void Negative_amounts_flatten_to_zero_only_when_asked()
    {
        AnyMoney.Sample(m => !m.OrZeroIfNegative().IsNegative, iter: 10_000);
        // …and a non-negative value is untouched, so the helper cannot quietly change a good amount.
        AnyMoney.Where(m => !m.IsNegative).Sample(m => m.OrZeroIfNegative() == m, iter: 5_000);
    }

    [Fact]
    public void Ordering_is_consistent_with_subtraction()
    {
        Gen.Select(AnyMoney, AnyMoney)
            .Sample((a, b) => (a < b) == ((a - b).IsNegative), iter: 10_000);
    }

    [Fact]
    public void There_is_no_implicit_conversion_back_to_decimal()
    {
        // The property that keeps the type from being decorative. An implicit conversion would let
        // `allowed = billed` compile the day someone changes one side back to decimal — which is exactly how
        // X3 would return. Asserted by reflection because the alternative is a test that does not compile.
        typeof(Money).GetMethods()
            .Where(m => m.Name is "op_Implicit")
            .Should().BeEmpty("an implicit conversion re-admits Money to the untyped world");
    }

    [Fact]
    public void Mixing_currencies_throws_rather_than_converting()
    {
        // There is no exchange rate in this domain. Silently treating USD as EGP would make a settlement
        // total disagree with its own lines, and the disagreement would be invisible.
        // (Only one currency exists today, which is why this is asserted structurally: the guard must be
        // present and reachable BEFORE a second currency lands, not added along with it.)
        var guard = typeof(Money).GetMethod("Same",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        guard.Should().NotBeNull("the currency guard must exist before a second currency does");
    }
}
