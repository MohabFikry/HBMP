using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// 30.5c — design 46 §7c's three cases, which "must not be collapsed".
///
/// <para>Each collapse has a distinct cost. Treating a missing check-in as a zero wait puts a fabricated
/// zero into the clinic manager's average. Reordering an out-of-order pair destroys the only signal that
/// reception is entering arrivals retroactively. Rendering a negative interval as "0 minutes" does both at
/// once, and looks like data.</para>
/// </summary>
public class VisitOpeningRulesTests
{
    private static readonly DateTimeOffset Arrived = new(2026, 8, 7, 9, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Seen = new(2026, 8, 7, 9, 47, 0, TimeSpan.Zero);

    [Fact]
    public void Checked_in_then_seen_gives_both_entries_and_the_waiting_time()
    {
        var opening = VisitOpeningRules.Compose(Arrived, Seen);

        opening.Kind.Should().Be(VisitOpeningKind.CheckedInThenSeen);
        opening.Waiting.Should().Be(TimeSpan.FromMinutes(37));
        opening.Flagged.Should().BeFalse();
    }

    [Fact]
    public void NO_CHECK_IN_RECORDED_is_its_own_case_and_never_a_zero_wait()
    {
        // "The timeline says 'no check-in recorded'; it does not silently begin at Visit started as though
        // the two were the same moment." A zero would be averaged into the dashboard as a real measurement.
        var opening = VisitOpeningRules.Compose(checkedInAt: null, visitStartedAt: Seen);

        opening.Kind.Should().Be(VisitOpeningKind.NoCheckInRecorded);
        opening.Waiting.Should().BeNull(
            "'we do not know how long they waited' and 'they did not wait' are different statements, and "
            + "only one of them is true");
        opening.VisitStartedAt.Should().Be(Seen, "the visit still shows — only the arrival is absent");
    }

    [Fact]
    public void A_check_in_recorded_AFTER_the_visit_started_is_flagged_not_reordered()
    {
        // A retroactive entry, and a real data-quality signal. "Silently sorting bad timestamps into a tidy
        // story is how you lose the ability to notice the process is broken."
        var opening = VisitOpeningRules.Compose(checkedInAt: Seen, visitStartedAt: Arrived);

        opening.Kind.Should().Be(VisitOpeningKind.RecordedOutOfOrder);
        opening.Flagged.Should().BeTrue();
        opening.CheckedInAt.Should().Be(Seen, "shown AS RECORDED");
        opening.VisitStartedAt.Should().Be(Arrived, "shown AS RECORDED");
        opening.Waiting.Should().BeNull(
            "a negative interval rendered as '0 minutes' is a data error wearing a measurement's clothes");
    }

    [Fact]
    public void A_patient_checked_in_but_not_yet_seen_has_no_waiting_time_YET()
    {
        // Still waiting. The number is not absent because of a data problem, so the case is the normal one —
        // there is simply no end to the interval yet.
        var opening = VisitOpeningRules.Compose(Arrived, visitStartedAt: null);

        opening.Kind.Should().Be(VisitOpeningKind.CheckedInThenSeen);
        opening.Waiting.Should().BeNull();
        opening.Flagged.Should().BeFalse();
    }

    [Fact]
    public void The_arithmetic_holds_ACROSS_MIDNIGHT()
    {
        // A late clinic. Computed on instants, not on clock faces, so the day boundary is not a special case.
        var lateArrival = new DateTimeOffset(2026, 8, 7, 23, 40, 0, TimeSpan.Zero);
        var afterMidnight = new DateTimeOffset(2026, 8, 8, 0, 25, 0, TimeSpan.Zero);

        VisitOpeningRules.Compose(lateArrival, afterMidnight).Waiting
            .Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void The_arithmetic_holds_ACROSS_A_TIMEZONE_OFFSET()
    {
        // Cairo local on one side, UTC on the other. An interval between two INSTANTS is offset-independent;
        // a subtraction done on local wall-clock components would report a two-hour wait here.
        var cairo = new DateTimeOffset(2026, 8, 7, 11, 10, 0, TimeSpan.FromHours(3));
        var utc = new DateTimeOffset(2026, 8, 7, 8, 47, 0, TimeSpan.Zero);

        VisitOpeningRules.Compose(cairo, utc).Waiting.Should().Be(TimeSpan.FromMinutes(37));
    }

    [Fact]
    public void Neither_recorded_reports_the_missing_check_in()
    {
        var opening = VisitOpeningRules.Compose(null, null);

        opening.Kind.Should().Be(VisitOpeningKind.NoCheckInRecorded);
        opening.Waiting.Should().BeNull();
    }
}
