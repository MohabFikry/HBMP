using FluentAssertions;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mersal.Emr.Tests;

/// <summary>
/// 25.3 (design 42 §3, §7 rule 6) — a lapsed licence FLAGS existing appointments. It never cancels one, and
/// it never touches the past.
///
/// <para>Driven through the consumer's REAL flagging method against a real database, rather than through a
/// restated copy of its predicate. A test that re-expresses the rule proves the test agrees with itself; the
/// thing worth proving is that the code someone will change next year still behaves this way.</para>
///
/// <para>The acceptance criterion this answers, verbatim from the design: <i>given a licence lapses with 12
/// future appointments, then all 12 are flagged for reassignment, none cancelled.</i></para>
/// </summary>
public class LicenceFlagNotCancelTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("EMR_TEST_DB_OWNER");

    private static EmrDbContext Ctx() =>
        new(new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private static readonly DateOnly Expiry = new(2026, 9, 30);

    /// <summary>Mid-morning on the expiry date, in UTC. Everything is positioned relative to this so "future"
    /// and "past" mean the same thing to the test and to the consumer.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 6, 0, 0, TimeSpan.Zero);

    private static PractitionerLicenceExpiredConsumer Consumer() =>
        new(scopeFactory: null!, new FixedClock(Now),
            Options.Create(new PractitionerLicenceExpiredOptions()),
            NullLogger<PractitionerLicenceExpiredConsumer>.Instance);

    private sealed class FixedClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private const string Tenant = "t-licence-gate";

    private static Appointment Appt(Guid doctor, Guid branch, DateTimeOffset start,
        AppointmentStatus status = AppointmentStatus.Booked) => new()
    {
        AppointmentId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(),
        // `ck_appointment_tenant_not_blank` — an appointment belonging to no tenant is invisible to every
        // real one and visible to any session binding an empty GUC (docs/HANDOFF.md). The constraint caught
        // this fixture omitting it.
        TenantId = Tenant,
        DoctorId = doctor, BranchId = branch,
        ScheduledStart = start, ScheduledEnd = start.AddMinutes(15),
        Status = status,
    };

    private static async Task Cleanup(Guid doctor)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM emr.appointment WHERE doctor_id = {0}", doctor);
    }

    [SkippableFact]
    public async Task TWELVE_future_appointments_are_all_flagged_and_NONE_cancelled()
    {
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                // Twelve, spread over the three weeks AFTER the licence lapses.
                for (var i = 1; i <= 12; i++)
                    db.Appointments.Add(Appt(doctor, branch, Now.AddDays(i * 2)));
                await db.SaveChangesAsync();
            }

            int flagged;
            await using (var db = Ctx())
            {
                flagged = await Consumer().FlagAsync(
                    db,
                    new PractitionerLicenceExpiredConsumer.LicenceExpiredEvent(Tenant, doctor, Expiry),
                    CancellationToken.None);
                await db.SaveChangesAsync();
            }

            flagged.Should().Be(12);

            await using (var verify = Ctx())
            {
                var rows = await verify.Appointments.AsNoTracking().Where(a => a.DoctorId == doctor).ToListAsync();

                rows.Should().HaveCount(12);
                rows.Should().OnlyContain(a => a.ReassignmentNeededAt != null, "all twelve are flagged");
                rows.Should().OnlyContain(a => a.Status == AppointmentStatus.Booked,
                    "NONE cancelled — an automated cancellation lands on someone who has arranged childcare " +
                    "and lost a day's pay to travel, and who cannot tell it from being dropped");
                rows.Should().NotContain(a => a.Status == AppointmentStatus.Cancelled);
            }
        }
        finally { await Cleanup(doctor); }
    }

    [SkippableFact]
    public async Task NEVER_RETROACTIVE_a_past_appointment_is_untouched()
    {
        // Care already given was given under a valid licence. Flagging it asks reception to act on something
        // that cannot be changed, and buries the appointments that can be.
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                db.Appointments.Add(Appt(doctor, branch, Now.AddDays(-30), AppointmentStatus.Completed));
                db.Appointments.Add(Appt(doctor, branch, Now.AddDays(-3)));
                db.Appointments.Add(Appt(doctor, branch, Now.AddDays(7)));      // the only affected one
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var flagged = await Consumer().FlagAsync(
                    db, new PractitionerLicenceExpiredConsumer.LicenceExpiredEvent(Tenant, doctor, Expiry),
                    CancellationToken.None);
                await db.SaveChangesAsync();
                flagged.Should().Be(1, "only the future one");
            }

            await using (var verify = Ctx())
            {
                var rows = await verify.Appointments.AsNoTracking()
                    .Where(a => a.DoctorId == doctor).OrderBy(a => a.ScheduledStart).ToListAsync();

                rows[0].ReassignmentNeededAt.Should().BeNull("a completed encounter from before expiry");
                rows[1].ReassignmentNeededAt.Should().BeNull("a past appointment already happened");
                rows[2].ReassignmentNeededAt.Should().NotBeNull();
            }
        }
        finally { await Cleanup(doctor); }
    }

    [SkippableFact]
    public async Task An_appointment_ON_the_expiry_date_is_left_alone()
    {
        // The boundary is inclusive: the licence is valid THROUGH 30 September, so that day's clinic is
        // lawful and must not be flagged. This is the assertion that keeps the consumer agreeing with
        // PractitionerLicence.IsValidAt.
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        try
        {
            // 15:00 Cairo on the expiry date (12:00Z), which is after `Now` — so it is a FUTURE appointment
            // and is excluded only because of the date rule, not because it is in the past.
            var sameDay = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
            // 09:00 Cairo the following morning — 06:00Z, which is still 30 September in UTC. This is the
            // case that would slip through a UTC-based cutoff: the first clinic of the day after expiry.
            var nextMorning = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);

            await using (var db = Ctx())
            {
                db.Appointments.Add(Appt(doctor, branch, sameDay));
                db.Appointments.Add(Appt(doctor, branch, nextMorning));
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var flagged = await Consumer().FlagAsync(
                    db, new PractitionerLicenceExpiredConsumer.LicenceExpiredEvent(Tenant, doctor, Expiry),
                    CancellationToken.None);
                await db.SaveChangesAsync();
                flagged.Should().Be(1, "only the one past the expiry date");
            }

            await using (var verify = Ctx())
            {
                var rows = await verify.Appointments.AsNoTracking()
                    .Where(a => a.DoctorId == doctor).OrderBy(a => a.ScheduledStart).ToListAsync();

                rows[0].ReassignmentNeededAt.Should().BeNull("30 September is covered by a 30 September expiry");
                rows[1].ReassignmentNeededAt.Should().NotBeNull(
                    "1 October is not — and a UTC cutoff would have missed this one, because 06:00Z on " +
                    "1 October Cairo is still 30 September in UTC");
            }
        }
        finally { await Cleanup(doctor); }
    }

    [SkippableFact]
    public async Task A_REDELIVERY_does_not_refresh_a_flag_a_receptionist_has_already_seen()
    {
        // At-least-once delivery. The timestamp records WHEN the problem arose; a redelivery must not make a
        // week-old orphan look like today's news and push it back to the top of a worklist.
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        try
        {
            await using (var db = Ctx()) { db.Appointments.Add(Appt(doctor, branch, Now.AddDays(5))); await db.SaveChangesAsync(); }

            var evt = new PractitionerLicenceExpiredConsumer.LicenceExpiredEvent(Tenant, doctor, Expiry);

            await using (var db = Ctx()) { await Consumer().FlagAsync(db, evt, CancellationToken.None); await db.SaveChangesAsync(); }

            DateTimeOffset? first;
            await using (var verify = Ctx())
                first = (await verify.Appointments.AsNoTracking().SingleAsync(a => a.DoctorId == doctor)).ReassignmentNeededAt;

            // A later redelivery, from a consumer whose clock has moved on.
            var later = new PractitionerLicenceExpiredConsumer(
                null!, new FixedClock(Now.AddDays(7)),
                Options.Create(new PractitionerLicenceExpiredOptions()),
                NullLogger<PractitionerLicenceExpiredConsumer>.Instance);

            int second;
            await using (var db = Ctx()) { second = await later.FlagAsync(db, evt, CancellationToken.None); await db.SaveChangesAsync(); }

            second.Should().Be(0, "the row is already flagged");
            await using (var verify = Ctx())
                (await verify.Appointments.AsNoTracking().SingleAsync(a => a.DoctorId == doctor))
                    .ReassignmentNeededAt.Should().Be(first, "the original timestamp survives");
        }
        finally { await Cleanup(doctor); }
    }

    [SkippableFact]
    public async Task A_CANCELLED_appointment_is_not_flagged()
    {
        // Nothing to reassign. Flagging it would put a dead row on a worklist someone has to triage.
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                db.Appointments.Add(Appt(doctor, branch, Now.AddDays(5), AppointmentStatus.Cancelled));
                await db.SaveChangesAsync();
            }
            await using (var db = Ctx())
            {
                var flagged = await Consumer().FlagAsync(
                    db, new PractitionerLicenceExpiredConsumer.LicenceExpiredEvent(Tenant, doctor, Expiry),
                    CancellationToken.None);
                flagged.Should().Be(0);
            }
        }
        finally { await Cleanup(doctor); }
    }
}
