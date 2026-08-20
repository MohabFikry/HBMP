using FluentAssertions;
using Mersal.Claims.Domain;
// Aliased because `Mersal.Money` is both the namespace and the type: a plain `using Mersal.Amounts;`
// makes `Money.Egp` resolve against the namespace and fail to compile.
using MoneyValue = Mersal.Amounts.Money;

namespace Mersal.Claims.Tests;

/// <summary>
/// 2026-08-09 audit §3 — "<c>Money.CapTo</c> is dead code while claims re-implements the clamp."
///
/// <para>Both halves are true, and the conclusion the phrasing invites — delete one — is wrong. There are two
/// clamps because there are two type worlds: <c>DecisionRules.Cap</c> works on the bare decimals claims
/// adjudication is still written in, and <c>Money.CapTo</c> is the same rule in the value type the platform
/// is migrating toward. Deleting <c>CapTo</c> would remove the thing that migration lands on; rewriting claims to
/// use it today is the migration itself, which is hundreds of signatures and the EF mapping layer.</para>
///
/// <para>What actually makes a duplicated rule dangerous is not that it exists — it is that the two copies can
/// drift while everyone assumes they have not. So this pins them together. If somebody changes one clamp, this
/// test names the other one, and the change becomes a decision instead of an accident.</para>
///
/// <para><b>The rule they both express.</b> A payable amount is capped by the contract tariff; with NO tariff
/// it is capped by what was billed rather than by infinity. That second clause is the interesting half and
/// the one an incomplete reading loses: absence of a tariff is not permission to pay anything.</para>
/// </summary>
public class TheTwoClampsAgreeTests
{
    /// <summary>Amounts around the boundaries that matter: equality with the ceiling, either side of it, zero,
    /// and a half-piastre where the two types' ROUNDING could disagree if one of them stopped being ToEven.</summary>
    public static TheoryData<decimal, decimal?> Cases() => new()
    {
        { 100.00m, 80.00m },     // billed above tariff  → tariff
        { 80.00m, 100.00m },     // billed below tariff  → billed
        { 100.00m, 100.00m },    // equal                → either
        { 100.00m, null },       // NO TARIFF            → billed, not infinity
        { 0m, 50.00m },
        { 0m, null },
        { 12.345m, 12.335m },    // both round to 2dp; a mode change on one side shows up here
        { 999999.99m, 0.01m },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_decimal_clamp_and_the_Money_clamp_produce_the_same_amount(decimal billed, decimal? tariff)
    {
        var viaDecimal = DecisionRules.Cap(billed, tariff);
        var viaMoney = MoneyValue.CapTo(MoneyValue.Egp(billed), tariff is { } t ? MoneyValue.Egp(t) : null);

        viaMoney.Should().Be(MoneyValue.Egp(viaDecimal),
            "the two clamps are the same rule in two type worlds; if one of them has changed, the other one "
            + "has to change with it or claims and everything that adopts Money will disagree about a payment");
    }

    [Fact]
    public void Neither_clamp_treats_a_missing_tariff_as_no_ceiling()
    {
        // Stated on its own because it is the clause a re-implementation drops. `CapTo(v, null) == v` reads
        // like "no ceiling"; it is only correct because the value handed in is ALREADY the billed amount.
        DecisionRules.Cap(500m, null).Should().Be(500m);
        MoneyValue.CapTo(MoneyValue.Egp(500m), null).Should().Be(MoneyValue.Egp(500m));

        // And the ceiling still bites when there is one, at every decision kind claims can pay on.
        foreach (var kind in new[] { ClaimDecisionKind.Approve, ClaimDecisionKind.PartiallyApprove, ClaimDecisionKind.Adjust })
        {
            var applied = DecisionRules.Apply(kind, allowed: 9_999m, billed: 500m, contractPrice: 120m);
            applied!.Value.Allowed.Should().Be(120m, "{0} must not pay above the tariff", kind);
        }
    }
}
