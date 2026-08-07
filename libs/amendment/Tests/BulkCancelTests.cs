using FluentAssertions;

namespace Mersal.Amendment.Tests;

/// <summary>
/// 30.1 — design 46 §3: "A whole-order cancel is simply 'cancel every still-cancellable line', and if some
/// lines are already consumed it reports PARTIAL SUCCESS plainly rather than failing the lot or silently
/// doing half."
///
/// <para>Both failure modes are worse than the truth. Failing the lot means a doctor who dispensed one of
/// three lines cannot withdraw the other two at all. Silently doing half means they believe they have.</para>
/// </summary>
public class BulkCancelTests
{
    private static readonly AmendContext Live = new(HeadAmendable: true, Expired: false);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    [Fact]
    public void All_lines_cancellable_is_a_full_success()
    {
        var result = BulkCancel.Plan([
            new AmendableLine(Id(1), false, 3, 0),
            new AmendableLine(Id(2), false, 3, 0),
        ], Live);

        result.Applied.Should().Be(2);
        result.Refused.Should().Be(0);
        result.IsPartial.Should().BeFalse();
        result.IsCompleteRefusal.Should().BeFalse();
    }

    [Fact]
    public void One_dispensed_line_of_three_yields_PARTIAL_SUCCESS_naming_which_and_why()
    {
        // The acceptance criterion from design 46 §10 and phase-30 Gate 1, verbatim.
        var result = BulkCancel.Plan([
            new AmendableLine(Id(1), IsTerminal: true, 3, 3),   // dispensed — fact
            new AmendableLine(Id(2), IsTerminal: false, 3, 0),
            new AmendableLine(Id(3), IsTerminal: false, 3, 0),
        ], Live);

        result.IsPartial.Should().BeTrue();
        result.Applied.Should().Be(2);
        result.Refused.Should().Be(1);

        result.Cancellable.Select(o => o.LineId).Should().Equal(Id(2), Id(3));
        // The refusal names the line AND the reason. "Some lines could not be cancelled" is not actionable.
        result.Outcomes.Single(o => o.LineId == Id(1)).Error
            .Should().Be(AmendabilityError.AlreadyTerminal);
    }

    [Fact]
    public void Every_line_already_consumed_is_a_complete_refusal_not_a_success_with_nothing_done()
    {
        // The distinction matters at the edge: a 200 with an empty cancelled-list reads as "done" on a
        // screen, and the doctor walks away believing an order was withdrawn that is still live.
        var result = BulkCancel.Plan([
            new AmendableLine(Id(1), IsTerminal: true, 3, 3),
            new AmendableLine(Id(2), IsTerminal: true, 3, 3),
        ], Live);

        result.Applied.Should().Be(0);
        result.Refused.Should().Be(2);
        result.IsCompleteRefusal.Should().BeTrue();
        result.IsPartial.Should().BeFalse();
    }

    [Fact]
    public void An_order_with_no_lines_at_all_is_a_complete_refusal_rather_than_a_silent_success()
    {
        var result = BulkCancel.Plan([], Live);

        result.Applied.Should().Be(0);
        result.IsCompleteRefusal.Should().BeTrue();
    }

    [Fact]
    public void An_expired_order_refuses_every_line_with_the_expiry_reason()
    {
        var result = BulkCancel.Plan([
            new AmendableLine(Id(1), false, 3, 0),
            new AmendableLine(Id(2), false, 3, 0),
        ], Live with { Expired = true });

        result.IsCompleteRefusal.Should().BeTrue();
        result.Outcomes.Should().OnlyContain(o => o.Error == AmendabilityError.Expired);
    }

    [Fact]
    public void A_partly_consumed_line_is_cancellable_and_counts_as_applied()
    {
        var result = BulkCancel.Plan([
            new AmendableLine(Id(1), IsTerminal: false, 6, 4),   // 4 delivered, 2 forfeited
            new AmendableLine(Id(2), IsTerminal: true, 3, 3),
        ], Live);

        result.IsPartial.Should().BeTrue();
        result.Cancellable.Select(o => o.LineId).Should().Equal(Id(1));
    }
}
