using FluentAssertions;
using Mersal.Prescribing;

namespace Mersal.Prescribing.Tests;

/// <summary>29.5 — turning an allocation into dated windows (design 45 §5).</summary>
public class WindowScheduleTests
{
    private static readonly DateOnly Start = new(2026, 4, 1);

    [Fact]
    public void Ninety_days_monthly_produces_three_windows_thirty_days_apart()
    {
        var windows = WindowSchedule.Build(
            ChronicAllocation.Split(270m, 3), Start, frequencyMonths: 1, durationDays: 90, toleranceDays: 5);

        windows.Should().HaveCount(3);
        windows.Select(w => w.ScheduledOpen).Should().Equal(Start, Start.AddDays(30), Start.AddDays(60));
        windows.Select(w => w.AllocatedQuantity).Should().Equal(90m, 90m, 90m);
    }

    [Fact]
    public void The_early_tolerance_moves_opens_at_but_never_the_scheduled_date()
    {
        // The SCHEDULED date is what the patient is told; opens_at is how early the counter will accept them.
        // Conflating them would tell every patient to come five days sooner and re-create the drift the fixed
        // window exists to prevent.
        var windows = WindowSchedule.Build(
            ChronicAllocation.Split(90m, 3), Start, frequencyMonths: 1, durationDays: 90, toleranceDays: 5);

        windows[1].ScheduledOpen.Should().Be(Start.AddDays(30));
        windows[1].OpensAt.Should().Be(Start.AddDays(25));
    }

    [Fact]
    public void The_first_window_opens_immediately_rather_than_five_days_before_the_script_existed()
    {
        // A tolerance applied to window 1 would put opens_at before the prescribing date — a window that was
        // already open before the doctor wrote it. Harmless in practice and nonsense in a report.
        var windows = WindowSchedule.Build(
            ChronicAllocation.Split(90m, 3), Start, frequencyMonths: 1, durationDays: 90, toleranceDays: 5);

        windows[0].OpensAt.Should().Be(Start);
    }

    [Fact]
    public void Windows_are_contiguous_and_the_last_one_ends_with_the_script()
    {
        // No gap between windows — a day nobody could collect on — and no overrun past the script's duration,
        // which would let a patient collect after the prescription itself expired.
        var windows = WindowSchedule.Build(
            ChronicAllocation.Split(90m, 3), Start, frequencyMonths: 1, durationDays: 90, toleranceDays: 5);

        windows[0].ClosesAt.Should().Be(windows[1].ScheduledOpen.AddDays(-1));
        windows[1].ClosesAt.Should().Be(windows[2].ScheduledOpen.AddDays(-1));
        windows[^1].ClosesAt.Should().Be(Start.AddDays(89), "the script runs 90 days from its start");
    }

    [Fact]
    public void Every_window_is_numbered_from_one()
    {
        var windows = WindowSchedule.Build(
            ChronicAllocation.Split(90m, 3), Start, frequencyMonths: 1, durationDays: 90, toleranceDays: 5);

        windows.Select(w => w.WindowNo).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void The_schedule_allocates_exactly_what_the_split_allocated()
    {
        // The invariant carried through to the dated form. A schedule that dropped or duplicated a window
        // would break the sum without touching the allocation that computed it.
        var split = ChronicAllocation.Split(100m, 3);
        var windows = WindowSchedule.Build(split, Start, frequencyMonths: 1, durationDays: 90, toleranceDays: 5);

        windows.Sum(w => w.AllocatedQuantity).Should().Be(100m);
        windows.Select(w => w.AllocatedQuantity).Should().Equal(34m, 33m, 33m);
    }
}
