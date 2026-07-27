using System.Globalization;

namespace Mersal.Money;

/// <summary>
/// Phase 18.F1 — a money value type, so the class of defect X3 belongs to cannot be written.
///
/// X3 was: a claim decision could set <c>allowed_amount</c> above the contract tariff, because the value was
/// a bare <c>decimal</c> and nothing about its type said "this is a capped amount". 18.A2 fixed it with a
/// clamp at each decision site. That is correct and it is also the fragile kind of fix — it holds until
/// someone adds a seventh decision kind and does not know the clamp exists.
///
/// This makes the invariant carryable by the value itself:
///   * A currency travels WITH the amount, so EGP cannot be added to USD. That is not hypothetical here —
///     UNHCR reimbursements and cross-border partners (interop 13.2) are on the roadmap, and the moment a
///     second currency exists a bare decimal silently sums them.
///   * Rounding is BANKER'S at 2dp, applied once at construction. Half-away-from-zero — .NET's default for
///     Math.Round(d, 2) is actually ToEven, but decimal.Round with MidpointRounding.AwayFromZero is what
///     most people reach for — biases every .005 upward, and across a settlement batch of thousands of
///     lines that is a systematic overpayment. ToEven has no directional bias.
///   * There is NO implicit conversion to or from decimal. Implicit conversion is what makes a value type
///     decorative: it lets `allowed = billed` compile after someone changes one side back to decimal, which
///     is precisely how the original defect would return.
///
/// Deliberately NOT here: arithmetic that could lose the currency (no operator* with another Money), and no
/// division — apportioning money needs an explicit remainder policy, and a silent `/` is how a batch total
/// stops matching the sum of its lines.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>The scale every amount on this platform is stored and settled at (22-data-dictionary:
    /// numeric(14,2)). Constructing at a different scale would let the database round differently from the
    /// application, which is how a total disagrees with its own lines.</summary>
    public const int Scale = 2;

    public decimal Amount { get; }
    public Currency Currency { get; }

    public Money(decimal amount, Currency currency)
    {
        // Round ONCE, at the boundary. Rounding at render (or worse, at each step) is how 3 × 0.005 becomes
        // 0.02 in one report and 0.01 in another, from the same stored value.
        Amount = decimal.Round(amount, Scale, MidpointRounding.ToEven);
        Currency = currency;
    }

    public static Money Egp(decimal amount) => new(amount, Currency.Egp);
    public static Money Zero(Currency currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;
    public bool IsNegative => Amount < 0m;

    // ---- arithmetic ------------------------------------------------------------------------------------

    /// <summary>Adding across currencies is a bug, not a conversion. There is no exchange rate in this
    /// domain and inventing one silently would be worse than failing.</summary>
    public static Money operator +(Money a, Money b) => new(a.Amount + Same(a, b).Amount, a.Currency);
    public static Money operator -(Money a, Money b) => new(a.Amount - Same(a, b).Amount, a.Currency);
    public static Money operator -(Money a) => new(-a.Amount, a.Currency);

    /// <summary>Money × quantity. The quantity is dimensionless, which is why this one is safe: 3 × EGP 5
    /// is EGP 15, whereas EGP 3 × EGP 5 has no meaning and is therefore not offered.</summary>
    public static Money operator *(Money a, decimal quantity) => new(a.Amount * quantity, a.Currency);
    public static Money operator *(decimal quantity, Money a) => a * quantity;

    public static bool operator <(Money a, Money b) => a.Amount < Same(a, b).Amount;
    public static bool operator >(Money a, Money b) => a.Amount > Same(a, b).Amount;
    public static bool operator <=(Money a, Money b) => a.Amount <= Same(a, b).Amount;
    public static bool operator >=(Money a, Money b) => a.Amount >= Same(a, b).Amount;

    public int CompareTo(Money other) => Amount.CompareTo(Same(this, other).Amount);

    /// <summary>
    /// The X3 invariant as a method: an approved amount never exceeds what the contract permits.
    ///
    /// A null cap means "no contract tariff on file", which caps at the billed amount rather than at
    /// infinity — the 18.A2 rule. Absence of a tariff is not permission to pay anything.
    /// </summary>
    public static Money CapTo(Money value, Money? ceiling) =>
        ceiling is { } c && value > c ? c : value;

    /// <summary>Clamp to zero. A negative allowed amount is not a refund — it is an arithmetic slip, and it
    /// should be flattened at the point it is constructed rather than travelling into a settlement.</summary>
    public Money OrZeroIfNegative() => IsNegative ? Zero(Currency) : this;

    private static Money Same(Money a, Money b) =>
        a.Currency == b.Currency
            ? b
            : throw new InvalidOperationException(
                $"cannot combine {a.Currency} and {b.Currency} — there is no exchange rate in this domain, " +
                "and converting silently would make a total disagree with its own lines");

    // ---- boundaries ------------------------------------------------------------------------------------

    /// <summary>The raw scalar, for persistence and for the wire. EXPLICIT on purpose: an implicit
    /// conversion would let a Money silently become a decimal and rejoin the untyped world, which is exactly
    /// what the type exists to prevent.</summary>
    public decimal ToDecimal() => Amount;

    /// <summary>Invariant-culture text for logs and audit reason codes — never for display. The UI formats
    /// with Intl in the active locale (18.D2 / U7); a server-formatted string cannot be re-localised.</summary>
    public override string ToString() =>
        $"{Currency.ToString().ToUpperInvariant()} {Amount.ToString("0.00", CultureInfo.InvariantCulture)}";
}

/// <summary>Currencies the platform settles in. EGP today; the enum exists so that adding a second one is a
/// compile-time event with a visible diff, not a silent widening of every existing amount.</summary>
public enum Currency
{
    Egp = 1,
}
