using FluentAssertions;
using Mersal.Amounts;
using Mersal.Finance.Domain;

namespace Mersal.Finance.Tests;

/// <summary>
/// ADR-0043 — the settlement's amounts are CALCULATED in <see cref="Money"/>.
///
/// <para>The type is not in the schema and there is no migration; the columns are still <c>numeric</c>. What
/// changed is that every product and sum on the settlement path now goes through a type that rounds once, at
/// the platform's settlement scale, and refuses to add two currencies. These tests pin the properties that
/// buys, because a design recorded only in an ADR is a design somebody can undo without noticing.</para>
/// </summary>
public class SettlementArithmeticIsMoneyTests
{
    private static Settlement WithLines(string currencyCode, params (decimal Unit, int Qty)[] lines)
    {
        var s = new Settlement { CurrencyCode = currencyCode };
        foreach (var (unit, qty) in lines)
            s.Lines.Add(new SettlementLine { AgreedUnitPrice = unit, DeliveredQty = qty, LineTotal = unit * qty });
        return s;
    }

    [Fact]
    public void A_settlements_currency_is_typed_and_an_unknown_code_is_refused_at_the_boundary()
    {
        WithLines("EGP").Currency.Should().Be(Currency.Egp);
        WithLines("egp").Currency.Should().Be(Currency.Egp, "an ISO code's case is not a different currency");

        // The case the property exists for. Defaulting to EGP here would turn a dollar amount into a pound
        // amount of the same magnitude — not a rounding error, a different sum of money.
        var usd = WithLines("USD");
        FluentActions.Invoking(() => usd.Currency).Should().Throw<InvalidOperationException>()
            .WithMessage("*not one this platform settles in*");

        var blank = WithLines("");
        FluentActions.Invoking(() => blank.Currency).Should().Throw<InvalidOperationException>()
            .WithMessage("*no currency code*");
    }

    [Fact]
    public void A_line_total_is_its_unit_price_times_quantity_in_the_settlements_currency()
    {
        var s = WithLines("EGP", (12.34m, 3));
        var line = s.Lines[0];

        line.UnitPriceIn(s.Currency).Should().Be(Money.Egp(12.34m));
        line.TotalIn(s.Currency).Should().Be(Money.Egp(37.02m));
    }

    [Fact]
    public void A_line_cannot_be_added_to_one_in_another_currency()
    {
        // The guarantee that only exists once amounts carry their currency. Today there is one currency and
        // this cannot happen; the point is that on the day there are two, this throws instead of summing.
        var line = new SettlementLine { AgreedUnitPrice = 10m, DeliveredQty = 1 };
        var egp = line.TotalIn(Currency.Egp);

        // Constructed rather than parsed, because `Currencies.Parse` refuses anything but EGP — which is the
        // outer defence. This exercises the inner one: Money's own refusal to combine.
        var other = new Money(10m, (Currency)999);
        FluentActions.Invoking(() => egp + other).Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot combine*");
    }

    [Fact]
    public void The_total_equals_the_sum_of_the_lines_where_a_decimal_sum_would_not()
    {
        // Three lines whose unrounded products each carry a half-piastre tail. Summed as raw decimals the
        // total is 0.015 above the sum of the lines a provider reads underneath it — a discrepancy of no
        // consequence in size and every consequence in kind, because nobody can point at where it came from.
        var s = WithLines("EGP", (0.005m, 1), (0.005m, 1), (0.005m, 1));

        var moneyTotal = s.Lines.Aggregate(Money.Zero(s.Currency), (running, l) => running + l.TotalIn(s.Currency));
        var displayedLines = s.Lines.Select(l => l.TotalIn(s.Currency).Amount).ToList();

        displayedLines.Should().AllBeEquivalentTo(0.00m, "0.005 rounds half-to-even to 0.00, not up to 0.01");
        moneyTotal.Amount.Should().Be(0.00m);
        moneyTotal.Amount.Should().Be(displayedLines.Sum(),
            "the total is a Money sum of the SAME Money the lines display, so the two cannot disagree");
    }

    [Fact]
    public void Rounding_is_bankers_at_the_settlement_scale()
    {
        // Not a restatement of the Money unit tests: this asserts the settlement path INHERITS that mode
        // rather than rounding its own way somewhere between the price book and the line.
        var s = WithLines("EGP", (2.345m, 1), (2.355m, 1));

        s.Lines[0].TotalIn(s.Currency).Amount.Should().Be(2.34m, "half to EVEN rounds .345 down to .34");
        s.Lines[1].TotalIn(s.Currency).Amount.Should().Be(2.36m, "and .355 up to .36 — no directional bias");
    }
}
