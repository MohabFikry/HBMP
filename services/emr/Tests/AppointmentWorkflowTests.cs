using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

public class AppointmentWorkflowTests
{
    [Theory]
    [InlineData(AppointmentStatus.Booked, AppointmentStatus.CheckedIn)]
    [InlineData(AppointmentStatus.Booked, AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.Booked, AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.CheckedIn, AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled)]
    public void Legal_transitions_are_allowed(AppointmentStatus from, AppointmentStatus to)
        => AppointmentWorkflow.CanTransition(from, to).Should().BeTrue();

    [Theory]
    [InlineData(AppointmentStatus.Completed, AppointmentStatus.Cancelled)]  // done — cannot cancel
    [InlineData(AppointmentStatus.CheckedIn, AppointmentStatus.NoShow)]      // checked in — never a no-show
    [InlineData(AppointmentStatus.Completed, AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.NoShow, AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled, AppointmentStatus.CheckedIn)]
    [InlineData(AppointmentStatus.Booked, AppointmentStatus.Completed)]      // must check in first
    public void Illegal_transitions_are_rejected(AppointmentStatus from, AppointmentStatus to)
        => AppointmentWorkflow.CanTransition(from, to).Should().BeFalse();

    [Fact]
    public void Booked_and_checkedin_hold_a_slot_others_do_not()
    {
        AppointmentWorkflow.HoldsSlot(AppointmentStatus.Booked).Should().BeTrue();
        AppointmentWorkflow.HoldsSlot(AppointmentStatus.CheckedIn).Should().BeTrue();
        AppointmentWorkflow.HoldsSlot(AppointmentStatus.Completed).Should().BeFalse();
        AppointmentWorkflow.HoldsSlot(AppointmentStatus.NoShow).Should().BeFalse();
        AppointmentWorkflow.HoldsSlot(AppointmentStatus.Cancelled).Should().BeFalse();
    }

    [Fact]
    public void NoShow_requires_a_passed_window_and_still_booked()
    {
        var grace = TimeSpan.FromMinutes(15);
        var start = DateTimeOffset.Parse("2026-07-23T09:00:00Z");
        var appt = new Appointment { Status = AppointmentStatus.Booked, ScheduledStart = start, ScheduledEnd = start.AddMinutes(15) };

        // Before window end + grace → cannot no-show.
        AppointmentWorkflow.CanNoShow(appt, start.AddMinutes(20), grace).Should().BeFalse();
        // After window end + grace → can no-show.
        AppointmentWorkflow.CanNoShow(appt, start.AddMinutes(31), grace).Should().BeTrue();
    }

    [Fact]
    public void NoShow_not_allowed_once_checked_in_even_if_time_passed()
    {
        var grace = TimeSpan.FromMinutes(15);
        var start = DateTimeOffset.Parse("2026-07-23T09:00:00Z");
        var appt = new Appointment { Status = AppointmentStatus.CheckedIn, ScheduledStart = start, ScheduledEnd = start.AddMinutes(15) };
        AppointmentWorkflow.CanNoShow(appt, start.AddHours(2), grace).Should().BeFalse();
    }

    [Fact]
    public void Reschedule_and_cancel_guards()
    {
        AppointmentWorkflow.CanReschedule(AppointmentStatus.Booked).Should().BeTrue();
        AppointmentWorkflow.CanReschedule(AppointmentStatus.CheckedIn).Should().BeFalse();
        AppointmentWorkflow.CanCancel(AppointmentStatus.Booked).Should().BeTrue();
        AppointmentWorkflow.CanCancel(AppointmentStatus.CheckedIn).Should().BeTrue();
        AppointmentWorkflow.CanCancel(AppointmentStatus.Completed).Should().BeFalse();
    }

    [Theory]
    [InlineData(AppointmentType.Referral, null, false)]                       // referral without REF → invalid
    [InlineData(AppointmentType.Referral, "REF-2026-000009", true)]
    [InlineData(AppointmentType.Scheduled, null, true)]                       // scheduled needs neither
    [InlineData(AppointmentType.WalkIn, null, true)]
    public void Referral_linkage_is_required(AppointmentType type, string? referralRef, bool expected)
        => AppointmentTypeLabels.LinkageSatisfied(type, referralRef, null).Should().Be(expected);

    [Fact]
    public void FollowUp_requires_origin_encounter()
    {
        AppointmentTypeLabels.LinkageSatisfied(AppointmentType.FollowUp, null, null).Should().BeFalse();
        AppointmentTypeLabels.LinkageSatisfied(AppointmentType.FollowUp, null, Guid.NewGuid()).Should().BeTrue();
    }
}
