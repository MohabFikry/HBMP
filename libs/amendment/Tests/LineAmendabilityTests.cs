using FluentAssertions;

namespace Mersal.Amendment.Tests;

/// <summary>
/// 30.1 — design 46 §3: <b>the amendable scope is whatever has not been consumed.</b>
///
/// <para>Every case in the design's table appears here as a test, because the table is the specification and
/// the rows differ in ways that are easy to collapse: a fully-dispensed line and a partly-dispensed one look
/// alike from the doctor's screen, and only one of them is fact.</para>
/// </summary>
public class LineAmendabilityTests
{
    private static readonly Guid L = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private static AmendableLine Line(decimal ordered, decimal consumed, bool terminal = false) =>
        new(L, terminal, ordered, consumed);

    private static readonly AmendContext Live = new(HeadAmendable: true, Expired: false);

    // ---------------------------------------------------------------- cancel

    [Fact]
    public void An_untouched_line_can_be_cancelled()
    {
        LineAmendability.ForCancel(Line(ordered: 3, consumed: 0), Live)
            .Should().Be(AmendabilityError.None);
    }

    [Fact]
    public void A_PARTLY_consumed_line_can_be_cancelled_and_that_forfeits_only_the_remainder()
    {
        // Design 46 §3, row 3: "6-session physiotherapy, 4 delivered → reduce to 4 delivered + 2 cancelled.
        // Delivered sessions stand." Cancelling here does NOT retract the four; it forfeits the two.
        LineAmendability.ForCancel(Line(ordered: 6, consumed: 4), Live)
            .Should().Be(AmendabilityError.None);
    }

    [Fact]
    public void A_FULLY_consumed_line_cannot_be_cancelled_because_it_is_fact()
    {
        // Design 46 §3, row 1: "line 1 dispensed → lines 2 and 3 only. Line 1 is fact."
        LineAmendability.ForCancel(Line(ordered: 3, consumed: 3, terminal: true), Live)
            .Should().Be(AmendabilityError.AlreadyTerminal);
    }

    [Fact]
    public void A_cancelled_or_superseded_line_cannot_be_cancelled_again()
    {
        LineAmendability.ForCancel(Line(ordered: 3, consumed: 0, terminal: true), Live)
            .Should().Be(AmendabilityError.AlreadyTerminal);
    }

    [Fact]
    public void An_EXPIRED_order_is_not_amendable_it_is_expired()
    {
        // Design 46 §7: "Bounded by the order's own validity — an expired order is not amendable, it is
        // expired." Its own error, not folded into OrderNotAmendable, because the recovery differs: the
        // approval team can revalidate an expired order, and nothing recovers a cancelled one.
        LineAmendability.ForCancel(Line(3, 0), Live with { Expired = true })
            .Should().Be(AmendabilityError.Expired);
    }

    [Fact]
    public void A_head_in_a_non_amendable_status_refuses_every_line()
    {
        LineAmendability.ForCancel(Line(3, 0), Live with { HeadAmendable = false })
            .Should().Be(AmendabilityError.OrderNotAmendable);
    }

    // ---------------------------------------------------------------- amend

    [Fact]
    public void Reducing_the_quantity_of_an_untouched_line_is_allowed()
    {
        LineAmendability.ForAmend(Line(ordered: 30, consumed: 0), newQuantity: 20, Live)
            .Should().Be(AmendabilityError.None);
    }

    [Fact]
    public void Increasing_the_quantity_is_allowed_here_and_judged_for_re_approval_elsewhere()
    {
        // Whether an increase needs a fresh authorisation is Gate 4's question, not this one. Refusing it
        // here would make an unapproved order unamendable and an approved one re-approvable — the exact
        // pair design 46 §5 says is costly to get backwards.
        LineAmendability.ForAmend(Line(ordered: 30, consumed: 0), newQuantity: 60, Live)
            .Should().Be(AmendabilityError.None);
    }

    [Fact]
    public void The_new_quantity_may_equal_what_has_already_been_consumed()
    {
        // The boundary case, and it is legal: "deliver exactly what has been delivered, and no more" is a
        // real clinical instruction — it is the amend form of forfeiting the remainder.
        LineAmendability.ForAmend(Line(ordered: 6, consumed: 4), newQuantity: 4, Live)
            .Should().Be(AmendabilityError.None);
    }

    [Fact]
    public void A_new_quantity_BELOW_what_was_consumed_is_refused_because_it_implies_un_dispensing()
    {
        // Invariant 4 / design 46 §4.4. The DB CHECK (consumed <= ordered) would also refuse it, but as a
        // 500 with a constraint name — this is the doctor's answer, and it names the reason.
        LineAmendability.ForAmend(Line(ordered: 6, consumed: 4), newQuantity: 3, Live)
            .Should().Be(AmendabilityError.BelowConsumed);
    }

    [Fact]
    public void A_zero_or_negative_quantity_is_not_an_amendment_it_is_a_cancellation()
    {
        LineAmendability.ForAmend(Line(3, 0), newQuantity: 0, Live)
            .Should().Be(AmendabilityError.InvalidQuantity);
        LineAmendability.ForAmend(Line(3, 0), newQuantity: -1, Live)
            .Should().Be(AmendabilityError.InvalidQuantity);
    }

    [Fact]
    public void An_amendment_that_changes_nothing_is_refused()
    {
        // Not pedantry: it would supersede a signed record, burn a version, publish an amendment event and
        // notify the pharmacy, the beneficiary and the case manager — all to say nothing happened.
        LineAmendability.ForAmend(Line(ordered: 30, consumed: 0), newQuantity: 30, Live)
            .Should().Be(AmendabilityError.NoChange);
    }

    [Fact]
    public void A_terminal_line_cannot_be_amended()
    {
        LineAmendability.ForAmend(Line(3, 0, terminal: true), newQuantity: 2, Live)
            .Should().Be(AmendabilityError.AlreadyTerminal);
    }

    [Fact]
    public void An_expired_order_cannot_be_amended_either()
    {
        LineAmendability.ForAmend(Line(3, 0), newQuantity: 2, Live with { Expired = true })
            .Should().Be(AmendabilityError.Expired);
    }
}
