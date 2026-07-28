using FluentAssertions;

namespace Mersal.Authz.Tests;

/// <summary>
/// 21.2 — SoD over per-membership OVERRIDES (design 40 §2).
///
/// Overrides hand out catalog keys; SoD is defined over duties. Bridging the two vocabularies is the whole
/// risk here: get it wrong in the safe-looking direction and the check silently never fires, leaving an
/// exception path that is the supported way to hold both halves of a duty the matrix splits.
/// </summary>
public class SodScopeGrantTests
{
    [Fact]
    public void Releasing_a_payment_conflicts_with_holding_the_right_to_initiate_one()
    {
        // Held as the FINE token, so this is a principal who can raise a payment but not release one —
        // exactly the separation the matrix draws. Granting the release key closes the loop.
        var violations = SegregationOfDuties.EvaluateScopeGrant(["finance:payment_initiate"], "finance:approve");

        violations.Should().NotBeEmpty();
        violations.Should().Contain(v => v.Reason.Contains("payment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Granting_a_duty_the_principal_already_holds_introduces_nothing()
    {
        // The coarse `finance` role already implies BOTH payment halves. An override naming one of them
        // changes nothing about what this person can do, so refusing it would block a no-op while leaving
        // the real problem — the role definition — untouched, and would train administrators to read the
        // SoD refusal as noise.
        SegregationOfDuties.EvaluateScopeGrant(["finance"], "finance:approve").Should().BeEmpty();
    }

    [Fact]
    public void Rate_setting_and_payment_release_cannot_be_combined()
    {
        // Rate manipulation + self-pay. network_team does NOT already carry the release half, so this is a
        // conflict the grant genuinely introduces.
        SegregationOfDuties.EvaluateScopeGrant(["network_team"], "finance:approve").Should().NotBeEmpty();
    }

    [Fact]
    public void Adjudicating_claims_conflicts_with_a_provider_affiliated_role()
    {
        // A provider deciding its own money.
        SegregationOfDuties.EvaluateScopeGrant(["doctor"], "claims:adjudicate").Should().NotBeEmpty();
    }

    [Fact]
    public void Submitting_and_adjudicating_claims_cannot_be_combined()
    {
        SegregationOfDuties.EvaluateScopeGrant(["claims_officer"], "claims:submit").Should().NotBeEmpty();
    }

    [Fact]
    public void An_ordinary_read_key_carries_no_separated_duty()
    {
        // The common case, and it must be a genuine "no conflict" rather than an unchecked one: reading a
        // lab result is not half of a separated duty.
        SegregationOfDuties.EvaluateScopeGrant(["doctor"], "emr:read").Should().BeEmpty();
        SegregationOfDuties.TokensForScope("emr:read").Should().BeEmpty();
    }

    [Fact]
    public void Every_mapped_key_resolves_to_a_token_the_conflict_matrix_actually_uses()
    {
        // A map entry pointing at a token no rule mentions is a dead branch that reads like a control. This
        // catches a typo in the map, which is otherwise invisible: the check would just stop firing.
        var known = SegregationOfDuties.ConflictRules
            .SelectMany(r => new[] { r.TokenA, r.TokenB })
            .ToHashSet(StringComparer.Ordinal);

        string[] mapped =
        [
            "finance:write", "finance:approve", "claims:submit", "claims:reimburse:submit",
            "claims:adjudicate", "claims:decide", "claims:review", "claims:settle",
            "beneficiary:merge", "beneficiary:merge:approve",
        ];

        foreach (var key in mapped)
        {
            var tokens = SegregationOfDuties.TokensForScope(key);
            tokens.Should().NotBeEmpty("{0} is in the map and must resolve to a duty token", key);
            tokens.Should().OnlyContain(t => known.Contains(t),
                "every token {0} maps to must appear in the conflict matrix", key);
        }
    }
}
