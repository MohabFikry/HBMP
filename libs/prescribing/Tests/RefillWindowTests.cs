using FluentAssertions;
using Mersal.Prescribing;

namespace Mersal.Prescribing.Tests;

/// <summary>
/// 29.5 / design 45 §5 — the refill-window lifecycle.
///
/// <para>The design decision under test: <b>the counter ENFORCES, the sweeper RECORDS</b>. Dispensability is
/// computed from the dates, so a stalled sweeper delays a forfeiture but can never turn a background-job
/// outage into a patient being refused at the counter — and equally can never let a window past its close be
/// collected, because the close date is in the predicate rather than in the sweeper.</para>
/// </summary>
public class RefillWindowTests
{
    private static readonly DateOnly Scheduled = new(2026, 4, 1);

    private static RefillWindow Window(
        WindowStatus status = WindowStatus.Pending, decimal allocated = 30m, decimal dispensed = 0m,
        int toleranceDays = 5) =>
        new(WindowNo: 1,
            ScheduledOpen: Scheduled,
            OpensAt: Scheduled.AddDays(-toleranceDays),
            ClosesAt: Scheduled.AddDays(29),
            AllocatedQuantity: allocated,
            DispensedQuantity: dispensed,
            Status: status);

    // ---- The early tolerance -------------------------------------------------------------------------------

    [Fact]
    public void A_window_cannot_be_dispensed_before_it_opens()
    {
        // Six days early: outside the five-day tolerance.
        var verdict = RefillWindows.MayDispense(Window(), on: Scheduled.AddDays(-6));

        verdict.Allowed.Should().BeFalse();
        verdict.Refusal.Should().Be(WindowRefusal.NotYetOpen);
    }

    [Fact]
    public void The_refusal_names_the_open_date_rather_than_being_generic()
    {
        // Design 45 §5: "attempting it is a clear refusal NAMING the open date, not a generic error." A
        // pharmacist has the beneficiary in front of them and has to be able to say when to come back.
        var verdict = RefillWindows.MayDispense(Window(), on: Scheduled.AddDays(-6));

        verdict.OpensAt.Should().Be(Scheduled.AddDays(-5));
    }

    [Fact]
    public void The_early_tolerance_lets_a_patient_collect_a_few_days_ahead()
    {
        // Exactly on the tolerance boundary, and one day inside it. The tolerance exists because a monthly
        // collection that must land on an exact date is one a working person cannot keep.
        RefillWindows.MayDispense(Window(), on: Scheduled.AddDays(-5)).Allowed.Should().BeTrue();
        RefillWindows.MayDispense(Window(), on: Scheduled.AddDays(-4)).Allowed.Should().BeTrue();
        RefillWindows.MayDispense(Window(), on: Scheduled).Allowed.Should().BeTrue();
    }

    // ---- Forfeiture ------------------------------------------------------------------------------------------

    [Fact]
    public void A_window_past_its_close_is_refused_by_the_COUNTER_even_if_the_sweeper_has_not_run()
    {
        // THE design point, first half. The window is still `Pending` — the sweeper has not marked it Missed —
        // and the counter refuses it anyway, because closes_at is in the predicate. Forfeiture is enforced by
        // the dates, not by a background job having got there first.
        var stillPending = Window(status: WindowStatus.Pending);

        var verdict = RefillWindows.MayDispense(stillPending, on: Scheduled.AddDays(30));

        verdict.Allowed.Should().BeFalse();
        verdict.Refusal.Should().Be(WindowRefusal.Missed);
    }

    [Fact]
    public void A_missed_window_cannot_be_recovered_later()
    {
        // "The quantity is FORFEITED and cannot be claimed later."
        var missed = Window(status: WindowStatus.Missed);

        RefillWindows.MayDispense(missed, on: Scheduled).Allowed.Should().BeFalse();
        RefillWindows.MayDispense(missed, on: Scheduled.AddDays(1)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_stalled_sweeper_never_prevents_a_legitimate_collection()
    {
        // THE design point, second half — and the reason `Open` is derived rather than written. If the sweeper
        // had to promote Pending → Open, an outage would leave every window Pending and a counter that keyed
        // on the status would turn a background-job failure into patients being turned away.
        var neverSwept = Window(status: WindowStatus.Pending);

        RefillWindows.MayDispense(neverSwept, on: Scheduled).Allowed.Should().BeTrue();
    }

    // ---- Blocked ≠ Missed --------------------------------------------------------------------------------------

    [Fact]
    public void A_blocked_window_is_refused_at_the_counter_but_resumes_when_eligibility_is_restored()
    {
        // "The script is BLOCKED, not cancelled, and resumes if eligibility is restored." A block is the system
        // stopping the patient; it must not consume their entitlement.
        var blocked = Window(status: WindowStatus.Blocked);

        RefillWindows.MayDispense(blocked, on: Scheduled).Allowed.Should().BeFalse();

        // Restored ⇒ back to Pending, and dispensable again while still inside its dates.
        var restored = RefillWindows.Unblock(blocked);
        restored.Status.Should().Be(WindowStatus.Pending);
        RefillWindows.MayDispense(restored, on: Scheduled).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Unblocking_after_the_window_closed_does_not_resurrect_it()
    {
        // Eligibility restored in month 3 does not give back month 2. The status returns to Pending, but the
        // dates have not moved and the counter still refuses.
        var restored = RefillWindows.Unblock(Window(status: WindowStatus.Blocked));

        RefillWindows.MayDispense(restored, on: Scheduled.AddDays(30)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Blocked_and_Missed_are_never_the_same_status()
    {
        // They are refused identically at the counter and must be reported differently: only one of them is
        // the patient's doing, and only the other should reach a case worker's queue.
        RefillWindows.MayDispense(Window(status: WindowStatus.Blocked), Scheduled).Refusal
            .Should().Be(WindowRefusal.Blocked);
        RefillWindows.MayDispense(Window(status: WindowStatus.Missed), Scheduled).Refusal
            .Should().Be(WindowRefusal.Missed);
    }

    // ---- Partial dispensing ------------------------------------------------------------------------------------

    [Fact]
    public void A_partially_dispensed_window_may_still_hand_over_its_remainder()
    {
        var partial = Window(status: WindowStatus.PartiallyDispensed, allocated: 30m, dispensed: 10m);

        var verdict = RefillWindows.MayDispense(partial, on: Scheduled);

        verdict.Allowed.Should().BeTrue();
        verdict.RemainingQuantity.Should().Be(20m);
    }

    [Fact]
    public void A_fully_dispensed_window_hands_over_nothing_more()
    {
        var done = Window(status: WindowStatus.Dispensed, allocated: 30m, dispensed: 30m);

        var verdict = RefillWindows.MayDispense(done, on: Scheduled);

        verdict.Allowed.Should().BeFalse();
        verdict.Refusal.Should().Be(WindowRefusal.AlreadyDispensed);
    }

    // ---- The sweeper -------------------------------------------------------------------------------------------

    [Fact]
    public void The_sweeper_forfeits_only_windows_that_closed_with_nothing_collected()
    {
        RefillWindows.ShouldForfeit(Window(), on: Scheduled.AddDays(30)).Should().BeTrue();
        RefillWindows.ShouldForfeit(Window(), on: Scheduled.AddDays(29)).Should().BeFalse("still open today");
        RefillWindows.ShouldForfeit(Window(dispensed: 30m, status: WindowStatus.Dispensed), Scheduled.AddDays(30))
            .Should().BeFalse("it was collected");
    }

    [Fact]
    public void The_sweeper_does_not_forfeit_a_partially_collected_window()
    {
        // A window the patient partly collected is not a no-show. Design 45 §5 forfeits the window that closes
        // UNDISPENSED; the uncollected remainder of a partial is a different question, and marking the whole
        // window Missed would misreport a beneficiary who did attend.
        RefillWindows.ShouldForfeit(
            Window(status: WindowStatus.PartiallyDispensed, dispensed: 10m), on: Scheduled.AddDays(30))
            .Should().BeFalse();
    }

    [Fact]
    public void Forfeiting_is_idempotent()
    {
        // The sweeper runs on a timer and may overlap itself. A second pass must match nothing rather than
        // rewriting a missed_at that an investigation may already be relying on.
        var missed = RefillWindows.Forfeit(Window(), at: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));

        missed.Status.Should().Be(WindowStatus.Missed);
        RefillWindows.ShouldForfeit(missed, on: Scheduled.AddDays(60)).Should().BeFalse();
    }

    [Fact]
    public void The_sweeper_never_forfeits_a_blocked_window()
    {
        // A blocked window was not the patient's failure to attend — it was the system refusing them. Sweeping
        // it to Missed would relabel the platform's own refusal as the beneficiary's no-show, and the case
        // team would lose the only signal that anything went wrong.
        RefillWindows.ShouldForfeit(Window(status: WindowStatus.Blocked), on: Scheduled.AddDays(30))
            .Should().BeFalse();
    }
}
