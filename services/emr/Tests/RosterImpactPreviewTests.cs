using FluentAssertions;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// 25.4 (design 42 §4) — the impact preview, against a real database.
///
/// <para>Acceptance criterion, verbatim: <i>given a closure over 8 booked appointments, then dryRun reports 8
/// and the apply flags 8, cancels 0.</i></para>
///
/// <para>Preview and apply call the SAME method — <see cref="RosterExceptionEndpoints.ImpactedAppointmentsAsync"/>
/// — which is what makes the parity structural rather than a coincidence two code paths happen to share. A
/// preview that does not match what apply does is worse than no preview: it is a number somebody signed off.</para>
/// </summary>
public class RosterImpactPreviewTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("EMR_TEST_DB_OWNER");

    private static EmrDbContext Ctx() =>
        new(new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private const string Tenant = "t-roster-impact";

    /// <summary>Tuesday 8 September 2026, 09:00 Cairo (06:00Z) — the day being closed.</summary>
    private static readonly DateOnly ClosureDay = new(2026, 9, 8);
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);

    private static Appointment Appt(Guid branch, Guid doctor, DateTimeOffset start,
        AppointmentStatus status = AppointmentStatus.Booked) => new()
    {
        AppointmentId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(), TenantId = Tenant,
        BranchId = branch, DoctorId = doctor,
        ScheduledStart = start, ScheduledEnd = start.AddMinutes(30), Status = status,
        BeneficiaryName = "Test Patient",
    };

    private static async Task Cleanup(Guid branch)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM emr.appointment WHERE branch_id = {0}", branch);
    }

    [SkippableFact]
    public async Task GIVEN_a_closure_over_EIGHT_booked_appointments_THEN_the_preview_reports_eight()
    {
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var branch = Guid.NewGuid();
        var doctor = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                // Eight on the closure day, half-hourly from 09:00 Cairo.
                for (var i = 0; i < 8; i++)
                    db.Appointments.Add(Appt(branch, doctor,
                        new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero).AddMinutes(30 * i)));

                // And the rows that must NOT be counted, each for a different reason:
                db.Appointments.Add(Appt(branch, doctor, new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero)));       // another day
                db.Appointments.Add(Appt(branch, doctor, new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero), AppointmentStatus.Cancelled));
                db.Appointments.Add(Appt(Guid.NewGuid(), doctor, new DateTimeOffset(2026, 9, 8, 7, 30, 0, TimeSpan.Zero))); // another branch
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var impacted = await RosterExceptionEndpoints.ImpactedAppointmentsAsync(
                    db, RosterExceptionKind.ClinicClosed, branch, practitionerId: null,
                    ClosureDay, ClosureDay, Now, CancellationToken.None);

                impacted.Should().HaveCount(8,
                    "eight booked appointments fall inside the closure — the other three are a different day, " +
                    "a cancelled row, and another branch");
                impacted.Should().OnlyContain(a => a.Status == AppointmentStatus.Booked);
            }
        }
        finally { await Cleanup(branch); }
    }

    [SkippableFact]
    public async Task AND_THE_APPLY_flags_the_same_eight_and_cancels_NONE()
    {
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var branch = Guid.NewGuid();
        var doctor = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                for (var i = 0; i < 8; i++)
                    db.Appointments.Add(Appt(branch, doctor,
                        new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero).AddMinutes(30 * i)));
                await db.SaveChangesAsync();
            }

            // The preview.
            int previewed;
            await using (var db = Ctx())
                previewed = (await RosterExceptionEndpoints.ImpactedAppointmentsAsync(
                    db, RosterExceptionKind.ClinicClosed, branch, null, ClosureDay, ClosureDay, Now, CancellationToken.None)).Count;

            // The apply — the same query, then the flagging the endpoint performs.
            await using (var db = Ctx())
            {
                var affected = await RosterExceptionEndpoints.ImpactedAppointmentsAsync(
                    db, RosterExceptionKind.ClinicClosed, branch, null, ClosureDay, ClosureDay, Now, CancellationToken.None);

                affected.Should().HaveCount(previewed, "DRY-RUN PARITY: the preview and the apply see the same set");

                foreach (var a in affected) { a.ReassignmentNeededAt = Now; a.UpdatedAt = Now; }
                await db.SaveChangesAsync();
            }

            await using (var verify = Ctx())
            {
                var rows = await verify.Appointments.AsNoTracking().Where(a => a.BranchId == branch).ToListAsync();
                rows.Should().HaveCount(8);
                rows.Count(a => a.ReassignmentNeededAt != null).Should().Be(8, "all eight flagged");
                rows.Count(a => a.Status == AppointmentStatus.Cancelled).Should().Be(0,
                    "ZERO cancelled — the system does not cancel a refugee's appointment, a person decides " +
                    "who covers the clinic (design 42 §7 rule 6)");
            }
        }
        finally { await Cleanup(branch); }
    }

    [SkippableFact]
    public async Task An_AD_HOC_clinic_impacts_nothing_because_it_ADDS_availability()
    {
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var branch = Guid.NewGuid();
        var doctor = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                db.Appointments.Add(Appt(branch, doctor, new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero)));
                await db.SaveChangesAsync();
            }
            await using (var db = Ctx())
            {
                var impacted = await RosterExceptionEndpoints.ImpactedAppointmentsAsync(
                    db, RosterExceptionKind.AdHocClinic, branch, null, ClosureDay, ClosureDay, Now, CancellationToken.None);
                impacted.Should().BeEmpty("adding a clinic strands nobody");
            }
        }
        finally { await Cleanup(branch); }
    }

    [SkippableFact]
    public async Task A_PAST_appointment_inside_the_closure_range_is_not_impacted()
    {
        // Closing a day that has partly happened must not ask reception to reassign the morning's completed
        // visits — never retroactive, the same rule the licence gate follows.
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var branch = Guid.NewGuid();
        var doctor = Guid.NewGuid();
        try
        {
            var midday = new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);
            await using (var db = Ctx())
            {
                db.Appointments.Add(Appt(branch, doctor, midday.AddHours(-3)));   // already happened
                db.Appointments.Add(Appt(branch, doctor, midday.AddHours(+3)));   // still to come
                await db.SaveChangesAsync();
            }
            await using (var db = Ctx())
            {
                var impacted = await RosterExceptionEndpoints.ImpactedAppointmentsAsync(
                    db, RosterExceptionKind.ClinicClosed, branch, null, ClosureDay, ClosureDay,
                    now: midday, CancellationToken.None);
                impacted.Should().HaveCount(1, "only the one still to come");
            }
        }
        finally { await Cleanup(branch); }
    }

    [SkippableFact]
    public async Task A_PRACTITIONER_only_exception_impacts_that_practitioner_across_branches()
    {
        // "Dr Hala is on leave" with no branch named: wherever she was due to work that day.
        Skip.If(Owner is null, "test DB not configured — set EMR_TEST_DB_OWNER to run this DB integration test.");
        var maadi = Guid.NewGuid();
        var dokki = Guid.NewGuid();
        var hala = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                db.Appointments.Add(Appt(maadi, hala, new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero)));
                db.Appointments.Add(Appt(dokki, hala, new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero)));
                db.Appointments.Add(Appt(maadi, Guid.NewGuid(), new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero)));
                await db.SaveChangesAsync();
            }
            await using (var db = Ctx())
            {
                var impacted = await RosterExceptionEndpoints.ImpactedAppointmentsAsync(
                    db, RosterExceptionKind.Leave, branchId: null, practitionerId: hala,
                    ClosureDay, ClosureDay, Now, CancellationToken.None);

                impacted.Should().HaveCount(2, "both of hers, at both clinics — and not the other clinician's");
                impacted.Should().OnlyContain(a => a.DoctorId == hala);
            }
        }
        finally { await Cleanup(maadi); await Cleanup(dokki); }
    }
}
