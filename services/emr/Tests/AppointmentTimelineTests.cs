using FluentAssertions;
using Mersal.Emr.Infrastructure;
using static Mersal.Emr.Infrastructure.AppointmentTimeline;

namespace Mersal.Emr.Tests;

/// <summary>
/// The timeline collapse rule. emr.appointment_history snapshots the WHOLE row on every insert and update, so
/// consecutive snapshots frequently share a status — a reschedule moves the times, not the state. One step per
/// snapshot would show the desk "Booked, Booked, Booked" and bury the transitions that actually matter.
/// </summary>
public class AppointmentTimelineTests
{
    // Minutes past 09:00 — added rather than passed as a minute field, so T(60) means 10:00 instead of throwing.
    private static DateTimeOffset T(int minutesPastNine) =>
        new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero).AddMinutes(minutesPastNine);

    [Fact]
    public void Only_status_CHANGES_become_steps()
    {
        var steps = Collapse(new[]
        {
            new HistoryProjection("Booked", T(0), null, "reception-a"),
            new HistoryProjection("Booked", T(5), "reception-a", "reception-a"),   // reschedule — same status
            new HistoryProjection("CheckedIn", T(10), "reception-b", "reception-a"),
            new HistoryProjection("CheckedIn", T(11), "reception-b", "reception-a"), // queue churn — same status
            new HistoryProjection("Completed", T(40), "doctor-c", "reception-a"),
        });

        // Newest first — the timeline answers "what just happened to this?" before "how did it start?".
        steps.Select(s => s.Status).Should().Equal("Completed", "CheckedIn", "Booked");
    }

    [Fact]
    public void The_opening_step_is_attributed_to_who_CREATED_it()
    {
        // The first snapshot's updated_by is null — nothing has updated it yet — so created_by is the only
        // honest attribution for "booked".
        var steps = Collapse(new[] { new HistoryProjection("Booked", T(0), null, "reception-a") });
        steps.Should().HaveCount(1);
        steps[0].By.Should().Be("reception-a");
    }

    [Fact]
    public void Later_steps_are_attributed_to_who_performed_THAT_transition()
    {
        var steps = Collapse(new[]
        {
            new HistoryProjection("Booked", T(0), null, "reception-a"),
            new HistoryProjection("CheckedIn", T(10), "reception-b", "reception-a"),
        });
        // Not the booker: the point of the timeline is who did each step. Index 0 is the LATEST step now
        // that the list reads newest-first.
        steps[0].By.Should().Be("reception-b");
    }

    [Fact]
    public void An_unattributed_transition_shows_no_actor_rather_than_the_wrong_one()
    {
        // Rows written before updated_by existed. Falling back to created_by here would claim the booker
        // checked the patient in, which is a lie the desk would act on.
        var steps = Collapse(new[]
        {
            new HistoryProjection("Booked", T(0), null, "reception-a"),
            new HistoryProjection("NoShow", T(30), null, "reception-a"),
        });
        steps[0].By.Should().BeNull();   // index 0 is the latest step (newest-first)
    }

    [Fact]
    public void Timestamps_come_from_the_snapshot_and_stay_in_order()
    {
        var steps = Collapse(new[]
        {
            new HistoryProjection("Booked", T(0), null, "a"),
            new HistoryProjection("CheckedIn", T(10), "b", "a"),
            new HistoryProjection("Completed", T(40), "c", "a"),
        });
        steps.Select(s => s.At).Should().BeInDescendingOrder().And.Equal(T(40), T(10), T(0));
    }

    [Fact]
    public void A_snapshot_with_no_status_is_skipped_rather_than_emitted_blank()
    {
        var steps = Collapse(new[]
        {
            new HistoryProjection(null, T(0), null, "a"),
            new HistoryProjection("", T(1), null, "a"),
            new HistoryProjection("Booked", T(2), null, "a"),
        });
        steps.Should().HaveCount(1);
        steps[0].Status.Should().Be("Booked");
    }

    [Fact]
    public void No_history_is_an_empty_timeline_not_a_failure()
    {
        Collapse(Array.Empty<HistoryProjection>()).Should().BeEmpty();
    }

    [Fact]
    public void A_status_that_RETURNS_is_a_new_step()
    {
        // NoShow → Booked is a legal rebooking on the same record (23 §6). It must show as its own step, not be
        // swallowed as "already seen Booked".
        var steps = Collapse(new[]
        {
            new HistoryProjection("Booked", T(0), null, "a"),
            new HistoryProjection("NoShow", T(30), "b", "a"),
            new HistoryProjection("Booked", T(60), "c", "a"),
        });
        steps.Select(s => s.Status).Should().Equal("Booked", "NoShow", "Booked");   // symmetric under reversal
    }

    [Fact]
    public void A_move_to_a_different_doctor_is_its_own_step()
    {
        // "Same time, different doctor" is something a `Rescheduled` step does not say, and it is exactly what
        // the desk has to tell the patient when they ring. Until the edit dialog could change a practitioner
        // this could not happen — the picker filtered to the appointment's own doctor — so nothing recorded it.
        var steps = AppointmentTimeline.Collapse(new[]
        {
            new AppointmentTimeline.HistoryProjection("Booked", T(0), null, "reception-a", "09:00", null, "dr-1"),
            new AppointmentTimeline.HistoryProjection("Booked", T(5), "reception-a", "reception-a", "09:00", null, "dr-2"),
        });

        steps.Should().HaveCount(2);
        steps[0].Status.Should().Be(AppointmentTimeline.DoctorChanged);
        steps[0].By.Should().Be("reception-a");
    }

    [Fact]
    public void A_move_that_changes_both_the_time_and_the_doctor_reports_the_RESCHEDULE()
    {
        // One step per act. The desk opening a timeline after a move asks "when is this now?" first, so the
        // time change is the headline; emitting both would put two rows against one edit and make the history
        // longer than the thing it describes.
        var steps = AppointmentTimeline.Collapse(new[]
        {
            new AppointmentTimeline.HistoryProjection("Booked", T(0), null, "reception-a", "09:00", null, "dr-1"),
            new AppointmentTimeline.HistoryProjection("Booked", T(5), "reception-a", "reception-a", "11:00", null, "dr-2"),
        });

        steps.Should().HaveCount(2);
        steps[0].Status.Should().Be(AppointmentTimeline.Rescheduled);
    }

    [Fact]
    public void An_unchanged_doctor_adds_nothing()
    {
        // The history trigger fires on every update, including ones the timeline says nothing about.
        var steps = AppointmentTimeline.Collapse(new[]
        {
            new AppointmentTimeline.HistoryProjection("Booked", T(0), null, "reception-a", "09:00", null, "dr-1"),
            new AppointmentTimeline.HistoryProjection("Booked", T(5), "reception-a", "reception-a", "09:00", null, "dr-1"),
        });

        steps.Should().HaveCount(1);
    }
}
