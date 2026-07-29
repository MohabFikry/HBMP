using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// The two rules that decide whether a doctor may start a visit from an appointment (23 §1). They live in the
/// encounter-creation path rather than behind the button, so a caller going straight to POST /encounters is
/// bound by them too — a rule enforced only where the UI happens to call it is not enforced.
/// </summary>
public class VisitStartRulesTests
{
    private static Appointment Appt(AppointmentStatus status, Guid? doctor) => new()
    {
        AppointmentId = Guid.NewGuid(), TenantId = "t", BeneficiaryId = Guid.NewGuid(),
        ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
        Status = status, DoctorId = doctor,
        ScheduledStart = DateTimeOffset.UtcNow, ScheduledEnd = DateTimeOffset.UtcNow.AddMinutes(15),
    };

    [Theory]
    [InlineData(AppointmentStatus.Booked)]
    [InlineData(AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.Completed)]
    public void A_visit_starts_only_from_CheckedIn(AppointmentStatus from)
    {
        // Starting from Booked records care for someone who is not in the building; from Cancelled or NoShow it
        // resurrects an appointment the desk already closed.
        VisitStartRules.MayStart(Appt(from, null), Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void A_CheckedIn_appointment_with_no_named_doctor_is_open_to_whoever_is_on_shift()
    {
        // A general clinic session belongs to the rota, not to a practitioner — that has to stay workable.
        VisitStartRules.MayStart(Appt(AppointmentStatus.CheckedIn, null), Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void The_assigned_doctor_may_start_their_own_visit()
    {
        var doctor = Guid.NewGuid();
        VisitStartRules.MayStart(Appt(AppointmentStatus.CheckedIn, doctor), doctor).Should().BeTrue();
    }

    [Fact]
    public void Another_doctor_may_NOT_start_someone_elses_visit()
    {
        var assigned = Guid.NewGuid();
        VisitStartRules.MayStart(Appt(AppointmentStatus.CheckedIn, assigned), Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void An_unidentifiable_caller_is_refused_a_named_appointment_rather_than_waved_through()
    {
        // Default-deny: a token whose subject is not a usable id must not fall through to "allowed".
        VisitStartRules.MayStart(Appt(AppointmentStatus.CheckedIn, Guid.NewGuid()), caller: null).Should().BeFalse();
    }
}
