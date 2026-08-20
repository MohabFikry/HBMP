using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Emr.Tests;

/// <summary>
/// THE daily-cap proof (0025, design 42 §7 rule 13). Fires N parallel bookings at DIFFERENT slots on one
/// doctor's day and asserts exactly <c>cap</c> succeed.
///
/// <para><b>Why this is a separate proof from the no-double-book one.</b> That test races bookers at ONE
/// slot, and the <c>FOR UPDATE</c> row lock is what makes it safe. Nothing about it protects a COUNT: twelve
/// bookers taking twelve different slots never contend on a row, so each transaction would read "19 booked",
/// each would see room under a cap of 20, and all twelve would commit. The doctor arrives to 31 patients and
/// every request returned 201.</para>
///
/// <para>That is why <see cref="AppointmentBookingService"/> takes a per-(doctor, day) advisory lock rather
/// than merely counting inside the transaction. Counting inside a transaction is not the same as counting
/// under a lock, and this test is the difference: remove the <c>pg_advisory_xact_lock</c> and it fails, while
/// every single-threaded capacity test keeps passing.</para>
/// </summary>
public class DailyCapacityConcurrencyTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("EMR_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static DbContextOptions<EmrDbContext> Options() =>
        new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [SkippableFact]
    public async Task Parallel_bookings_on_one_doctors_day_stop_exactly_at_the_cap()
    {
        Skip.If(Db is null, "test DB not configured — set EMR_TEST_DB to run this DB integration test.");

        const int cap = 4;
        const int racers = 14;

        var doctorId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var locationId = Guid.NewGuid();

        // A clinic day far enough out that it cannot collide with anything else the suite books, pinned to
        // mid-morning Cairo so the appointment's clinic date is unambiguous either side of midnight UTC —
        // then normalized to UTC, because Npgsql refuses to write a non-zero offset to timestamptz. The
        // instant is identical; only its representation changes, and the count query converts back with
        // AT TIME ZONE 'Africa/Cairo'.
        var clinicDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(90));
        var dayStart = new DateTimeOffset(clinicDate.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(2))
            .ToUniversalTime();

        // One slot per racer — DIFFERENT slots, which is the whole point.
        var slots = Enumerable.Range(0, racers)
            .Select(i => (Id: Guid.NewGuid(), Start: dayStart.AddMinutes(15 * i)))
            .ToList();

        await SeedSlotsAsync(slots, providerId, locationId, doctorId);
        try
        {
            var barrier = new TaskCompletionSource();
            var tasks = slots.Select(async slot =>
            {
                await barrier.Task;   // release all at once
                await using var ctx = new EmrDbContext(Options());
                var booking = new AppointmentBookingService(ctx);
                var appt = new Appointment
                {
                    AppointmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = Guid.NewGuid(),
                    ProviderId = providerId, LocationId = locationId, DoctorId = doctorId, SlotId = slot.Id,
                    AppointmentType = AppointmentType.Scheduled, Status = AppointmentStatus.Booked,
                    ScheduledStart = slot.Start, ScheduledEnd = slot.Start.AddMinutes(15),
                    IdempotencyKey = $"cap-race-{Guid.NewGuid()}",
                    CreatedAt = slot.Start, UpdatedAt = slot.Start,
                };
                return await booking.BookAsync(appt, capacity: new DailyCapacityCheck(doctorId, clinicDate, cap));
            }).ToList();

            barrier.SetResult();
            var results = await Task.WhenAll(tasks);

            results.Count(r => r.Outcome == BookOutcome.Booked).Should().Be(cap,
                "the cap is a limit on the day, and fourteen bookers arriving at once does not raise it");
            results.Count(r => r.Outcome == BookOutcome.DailyCapacityReached).Should().Be(racers - cap);

            // The refusals carry usable numbers rather than a bare "full".
            results.Where(r => r.Outcome == BookOutcome.DailyCapacityReached)
                .Should().OnlyContain(r => r.Cap == cap && r.Booked >= cap);

            // And the datastore agrees — the assertion that would catch a lock that serialized nothing.
            await using var verify = new EmrDbContext(Options());
            var live = await verify.Appointments.CountAsync(a =>
                a.DoctorId == doctorId
                && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn));
            live.Should().Be(cap);
        }
        finally { await CleanupAsync(doctorId, slots.Select(s => s.Id)); }
    }

    [SkippableFact]
    public async Task A_cancelled_appointment_gives_its_place_back()
    {
        Skip.If(Db is null, "test DB not configured — set EMR_TEST_DB to run this DB integration test.");

        const int cap = 1;
        var doctorId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var clinicDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(91));
        var dayStart = new DateTimeOffset(clinicDate.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(2))
            .ToUniversalTime();

        var slots = new List<(Guid Id, DateTimeOffset Start)>
        {
            (Guid.NewGuid(), dayStart),
            (Guid.NewGuid(), dayStart.AddMinutes(15)),
        };

        await SeedSlotsAsync(slots, providerId, locationId, doctorId);
        try
        {
            await using var ctx = new EmrDbContext(Options());
            var booking = new AppointmentBookingService(ctx);
            var check = new DailyCapacityCheck(doctorId, clinicDate, cap);

            var first = await booking.BookAsync(Appt(slots[0]), capacity: check);
            first.Outcome.Should().Be(BookOutcome.Booked);

            var second = await booking.BookAsync(Appt(slots[1]), capacity: check);
            second.Outcome.Should().Be(BookOutcome.DailyCapacityReached);

            // Cancel the first. A coordinator ringing round to rearrange a day is relying on exactly this:
            // counting cancelled appointments against the cap would make a full day impossible to unblock.
            first.Appointment!.Status = AppointmentStatus.Cancelled;
            await ctx.SaveChangesAsync();

            await using var fresh = new EmrDbContext(Options());
            var retry = await new AppointmentBookingService(fresh).BookAsync(Appt(slots[1]), capacity: check);
            retry.Outcome.Should().Be(BookOutcome.Booked);
        }
        finally { await CleanupAsync(doctorId, slots.Select(s => s.Id)); }

        Appointment Appt((Guid Id, DateTimeOffset Start) slot) => new()
        {
            AppointmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = Guid.NewGuid(),
            ProviderId = providerId, LocationId = locationId, DoctorId = doctorId, SlotId = slot.Id,
            AppointmentType = AppointmentType.Scheduled, Status = AppointmentStatus.Booked,
            ScheduledStart = slot.Start, ScheduledEnd = slot.Start.AddMinutes(15),
            IdempotencyKey = $"cap-{Guid.NewGuid()}", CreatedAt = slot.Start, UpdatedAt = slot.Start,
        };
    }

    /// <summary>The lock key must be the same in every process, or the lock serializes nothing while every
    /// single-process test passes. .NET randomizes string hashing per process, which is why it is derived
    /// from the GUID's bytes instead.</summary>
    [Fact]
    public void The_advisory_lock_key_is_stable_and_distinguishes_doctors_and_days()
    {
        var doctor = new Guid("aaaaaaaa-1111-2222-3333-444444444444");
        var other = new Guid("bbbbbbbb-1111-2222-3333-444444444444");
        var day = new DateOnly(2026, 9, 14);

        AppointmentBookingService.AdvisoryKey(doctor, day)
            .Should().Be(AppointmentBookingService.AdvisoryKey(doctor, day));
        AppointmentBookingService.AdvisoryKey(doctor, day)
            .Should().NotBe(AppointmentBookingService.AdvisoryKey(other, day));
        AppointmentBookingService.AdvisoryKey(doctor, day)
            .Should().NotBe(AppointmentBookingService.AdvisoryKey(doctor, day.AddDays(1)));
    }

    private static async Task SeedSlotsAsync(
        IEnumerable<(Guid Id, DateTimeOffset Start)> slots, Guid providerId, Guid locationId, Guid doctorId)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        foreach (var (id, start) in slots)
        {
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO emr.appointment_slot (slot_id, tenant_id, provider_id, location_id, doctor_id, slot_start, slot_end)
                  VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(Tenant);
            cmd.Parameters.AddWithValue(providerId);
            cmd.Parameters.AddWithValue(locationId);
            cmd.Parameters.AddWithValue(doctorId);
            cmd.Parameters.AddWithValue(start);
            cmd.Parameters.AddWithValue(start.AddMinutes(15));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task CleanupAsync(Guid doctorId, IEnumerable<Guid> slotIds)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using (var delAppt = new NpgsqlCommand("DELETE FROM emr.appointment WHERE doctor_id = $1", conn))
        {
            delAppt.Parameters.AddWithValue(doctorId);
            await delAppt.ExecuteNonQueryAsync();
        }
        await using var delSlot = new NpgsqlCommand("DELETE FROM emr.appointment_slot WHERE slot_id = ANY($1)", conn);
        delSlot.Parameters.AddWithValue(slotIds.ToArray());
        await delSlot.ExecuteNonQueryAsync();
    }
}
