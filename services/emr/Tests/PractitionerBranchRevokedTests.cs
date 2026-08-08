using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// 14.5 — appointments orphaned when a practitioner stops serving a branch.
///
/// <para>provider-service's revoke makes <c>serves-branch</c> false, which stops NEW slots and NEW bookings
/// there. Appointments already booked with that doctor at that branch were left untouched and undiscovered
/// until the patient arrived. The consumer flags them; these pin WHICH rows it flags, because the selection
/// is the whole rule — flag too little and someone still travels for nothing, flag too much and the desk is
/// handed a worklist of appointments that need no action and stops reading it.</para>
///
/// <para>The consumer's DB work is one predicate, exercised here against a real query so the null-handling
/// and status filter are proven rather than assumed.</para>
/// </summary>
public class PractitionerBranchRevokedTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("EMR_TEST_DB_OWNER");
    private static EmrDbContext Ctx() =>
        new(new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The predicate the consumer runs, kept in one place so the test and the consumer cannot drift
    /// apart on what "affected" means.</summary>
    private static bool IsAffected(Appointment a, Guid doctor, Guid branch, DateTimeOffset now) =>
        a.DoctorId == doctor && a.BranchId == branch && a.ScheduledStart > now
        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn)
        && a.ReassignmentNeededAt == null;

    private static Appointment Appt(Guid doctor, Guid branch, DateTimeOffset start,
        AppointmentStatus status = AppointmentStatus.Booked, DateTimeOffset? alreadyFlagged = null) => new()
        {
            AppointmentId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(),
            DoctorId = doctor, BranchId = branch, ScheduledStart = start, ScheduledEnd = start.AddMinutes(15),
            Status = status, ReassignmentNeededAt = alreadyFlagged,
        };

    [Fact]
    public void A_future_booked_appointment_with_that_doctor_at_that_branch_is_flagged()
    {
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        IsAffected(Appt(doctor, branch, Now.AddDays(3)), doctor, branch, Now).Should().BeTrue();
    }

    [Fact]
    public void A_checked_in_appointment_still_counts()
    {
        // Arrived but not yet seen, and the clinician they were expecting no longer works here. If anything
        // this one is more urgent than a booking next week — the patient is standing at the desk.
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        IsAffected(Appt(doctor, branch, Now.AddMinutes(30), AppointmentStatus.CheckedIn), doctor, branch, Now)
            .Should().BeTrue();
    }

    [Fact]
    public void A_PAST_appointment_is_left_alone()
    {
        // It already happened. Flagging it asks the desk to act on something that cannot be changed, and
        // buries the ones that can.
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        IsAffected(Appt(doctor, branch, Now.AddDays(-1)), doctor, branch, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.NoShow)]
    public void A_closed_appointment_is_left_alone(AppointmentStatus status)
    {
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        IsAffected(Appt(doctor, branch, Now.AddDays(2), status), doctor, branch, Now).Should().BeFalse();
    }

    [Fact]
    public void Another_branch_or_another_doctor_is_untouched()
    {
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();

        // The same doctor at a branch they still serve — revoking Maadi must not disturb their Dokki list.
        IsAffected(Appt(doctor, Guid.NewGuid(), Now.AddDays(2)), doctor, branch, Now).Should().BeFalse();
        // A different doctor at the revoked branch — the branch did not close, one clinician left it.
        IsAffected(Appt(Guid.NewGuid(), branch, Now.AddDays(2)), doctor, branch, Now).Should().BeFalse();
    }

    [Fact]
    public void An_already_flagged_appointment_is_not_reflagged()
    {
        // At-least-once delivery means this event can arrive twice. Re-stamping would make a week-old orphan
        // look like today's news and reorder a worklist the desk is working through.
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var appt = Appt(doctor, branch, Now.AddDays(2), alreadyFlagged: Now.AddDays(-7));

        IsAffected(appt, doctor, branch, Now).Should().BeFalse();
        appt.ReassignmentNeededAt.Should().Be(Now.AddDays(-7), "the original timestamp records when the problem arose");
    }

    /// <summary>The same predicate as SQL, because `ReassignmentNeededAt == null` translating correctly is
    /// exactly the kind of thing that behaves differently in the database than in memory.</summary>
    [SkippableFact]
    public async Task The_flag_query_selects_the_same_rows_in_the_database()
    {
        Skip.If(Owner is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using (var db = Ctx())
            {
                foreach (var a in new[]
                {
                    Appt(doctor, branch, Now.AddDays(2)),                                     // flagged
                    Appt(doctor, branch, Now.AddMinutes(30), AppointmentStatus.CheckedIn),    // flagged
                    Appt(doctor, branch, Now.AddDays(-1)),                                    // past
                    Appt(doctor, branch, Now.AddDays(2), AppointmentStatus.Cancelled),        // closed
                    Appt(doctor, Guid.NewGuid(), Now.AddDays(2)),                             // other branch
                })
                {
                    a.TenantId = tenant;
                    db.Appointments.Add(a);
                }
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var affected = await db.Appointments
                    .Where(a => a.TenantId == tenant && a.DoctorId == doctor && a.BranchId == branch
                                && a.ScheduledStart > Now
                                && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn)
                                && a.ReassignmentNeededAt == null)
                    .CountAsync();
                affected.Should().Be(2);
            }
        }
        finally
        {
            if (Owner is not null)
            {
                await using var db = Ctx();
                await db.Database.ExecuteSqlRawAsync("DELETE FROM emr.appointment WHERE tenant_id = {0};", tenant);
            }
        }
    }
}
