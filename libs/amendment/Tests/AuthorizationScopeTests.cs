using FluentAssertions;

namespace Mersal.Amendment.Tests;

/// <summary>
/// 30.4 — does the amendment stay inside what was approved (design 46 §5)?
///
/// <para><b>Getting this backwards is costly in BOTH directions</b>, which is why every test below has a
/// mirror: treat every amendment as re-approvable and the approval queue floods, so reviewers start
/// rubber-stamping; treat none as re-approvable and you have built a way to obtain an approval for one thing
/// and dispense another.</para>
/// </summary>
public class AuthorizationScopeTests
{
    private static ApprovedScope Approved(decimal? qty = 30, int? days = 90, params string[] codes) =>
        new(codes.Length == 0 ? ["80053"] : codes, qty, days);

    // ---------------------------------------------------------------- within scope

    [Fact]
    public void Reducing_the_quantity_stays_within_the_approved_scope()
    {
        AuthorizationScope.Assess(new AmendedScope("80053", 20, 90), Approved())
            .Should().Be(AuthorizationImpact.WithinApprovedScope);
    }

    [Fact]
    public void Shortening_the_duration_stays_within_the_approved_scope()
    {
        AuthorizationScope.Assess(new AmendedScope("80053", 30, 60), Approved())
            .Should().Be(AuthorizationImpact.WithinApprovedScope);
    }

    [Fact]
    public void Amending_to_EXACTLY_what_was_approved_is_within_it()
    {
        // The boundary. Approving 30 and dispensing 30 is the normal case, so an amendment that lands on the
        // approved number must not be treated as exceeding it.
        AuthorizationScope.Assess(new AmendedScope("80053", 30, 90), Approved())
            .Should().Be(AuthorizationImpact.WithinApprovedScope);
    }

    // ---------------------------------------------------------------- beyond scope

    [Fact]
    public void Increasing_the_quantity_leaves_the_approved_scope()
    {
        // The approval was a judgement about 30. It says nothing about 60, and treating it as though it did
        // is how you obtain an approval for one thing and dispense another.
        AuthorizationScope.Assess(new AmendedScope("80053", 60, 90), Approved())
            .Should().Be(AuthorizationImpact.BeyondApprovedScope);
    }

    [Fact]
    public void Extending_the_duration_leaves_the_approved_scope()
    {
        AuthorizationScope.Assess(new AmendedScope("80053", 30, 120), Approved())
            .Should().Be(AuthorizationImpact.BeyondApprovedScope);
    }

    [Fact]
    public void Changing_the_code_leaves_the_approved_scope_even_when_the_quantity_falls()
    {
        // A DIFFERENT service or drug, in a smaller amount, is still a different thing. The quantity going
        // down does not make an unapproved molecule approved — this is the case where a purely numeric
        // comparison would wave through a substitution nobody reviewed.
        AuthorizationScope.Assess(new AmendedScope("85025", 5, 30), Approved())
            .Should().Be(AuthorizationImpact.BeyondApprovedScope);
    }

    // ---------------------------------------------------------------- no authorisation at all

    [Fact]
    public void An_order_that_carried_no_authorisation_troubles_nobody()
    {
        // Most orders are not gated. There is no approval to invalidate, so the amendment is neither within
        // nor beyond one — and reporting it as "beyond" would flood the queue with items nobody ever
        // reviewed in the first place.
        AuthorizationScope.Assess(new AmendedScope("80053", 999, 999), approved: null)
            .Should().Be(AuthorizationImpact.NotAuthorized);
    }

    // ---------------------------------------------------------------- absent dimensions

    [Fact]
    public void An_approval_that_named_no_quantity_does_not_constrain_the_quantity()
    {
        // A reviewer who approved "a CMP" without a number approved the code, not an amount. Inventing a
        // ceiling they did not set would refuse amendments they never objected to.
        AuthorizationScope.Assess(new AmendedScope("80053", 500, 30), Approved(qty: null))
            .Should().Be(AuthorizationImpact.WithinApprovedScope);
    }

    [Fact]
    public void An_amendment_that_names_no_duration_is_judged_on_its_other_dimensions()
    {
        AuthorizationScope.Assess(new AmendedScope("80053", 20, null), Approved())
            .Should().Be(AuthorizationImpact.WithinApprovedScope);
        AuthorizationScope.Assess(new AmendedScope("80053", 90, null), Approved())
            .Should().Be(AuthorizationImpact.BeyondApprovedScope);
    }

    [Fact]
    public void An_approval_with_no_codes_at_all_constrains_nothing_by_code()
    {
        // An empty approved set is not "nothing is approved" — it is an approval that did not itemise, which
        // is what a whole-order approval looks like. Reading it as an empty allow-list would send every
        // amendment of an approved order back to the queue.
        AuthorizationScope.Assess(new AmendedScope("80053", 20, 30), new ApprovedScope([], 30, 90))
            .Should().Be(AuthorizationImpact.WithinApprovedScope);
    }

    // ---------------------------------------------------------------- the shared subset predicate

    [Fact]
    public void The_subset_predicate_is_order_insensitive_and_de_duplicated()
    {
        // The SAME predicate approvals' ValidatePartialScope uses. One notion of "inside the approved set",
        // not two — see the note on AuthorizationScope.
        AuthorizationScope.IsSubsetOfApproved(["80053", "70450"], ["70450", "80053", "85025"])
            .Should().BeTrue();
        AuthorizationScope.IsSubsetOfApproved(["80053", "80053"], ["80053"]).Should().BeTrue();
        AuthorizationScope.IsSubsetOfApproved(["99999"], ["80053"]).Should().BeFalse();
    }

    [Fact]
    public void Code_comparison_is_ORDINAL_because_a_service_code_is_an_identifier()
    {
        // "80053" and "80053 " are different codes, and a culture-sensitive or case-insensitive comparison
        // would quietly equate codes that master data treats as distinct.
        AuthorizationScope.IsSubsetOfApproved(["80053 "], ["80053"]).Should().BeFalse();
    }
}
