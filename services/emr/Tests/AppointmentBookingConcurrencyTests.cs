using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Emr.Tests;

/// <summary>THE no-double-book proof (3.1 guardrail). Fires N parallel bookings at ONE slot against a real
/// Postgres (migrations 0002 applied) and asserts EXACTLY ONE succeeds — the rest get SlotTaken. Env-gated so
/// DB-less CI skips: set EMR_TEST_DB to a conn string for the emr schema owner.</summary>
public class AppointmentBookingConcurrencyTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("EMR_TEST_DB");

    private static DbContextOptions<EmrDbContext> Options() =>
        new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [Fact]
    public async Task Parallel_bookings_at_one_slot_yield_exactly_one_success()
    {
        if (Db is null) return; // skip when no DB configured

        var providerId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await Seed(slotId, providerId, locationId, start);
        try
        {
            const int racers = 12;
            var barrier = new TaskCompletionSource();
            var tasks = Enumerable.Range(0, racers).Select(async i =>
            {
                await barrier.Task; // release all at once
                await using var ctx = new EmrDbContext(Options());
                var booking = new AppointmentBookingService(ctx);
                var appt = new Appointment
                {
                    AppointmentId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(),
                    ProviderId = providerId, LocationId = locationId, SlotId = slotId,
                    AppointmentType = AppointmentType.Scheduled, Status = AppointmentStatus.Booked,
                    ScheduledStart = start, ScheduledEnd = start.AddMinutes(15),
                    IdempotencyKey = $"race-{Guid.NewGuid()}", CreatedAt = start, UpdatedAt = start,
                };
                return await booking.BookAsync(appt);
            }).ToList();

            barrier.SetResult();
            var results = await Task.WhenAll(tasks);

            results.Count(r => r.Outcome == BookOutcome.Booked).Should().Be(1, "a slot holds at most one active appointment");
            results.Count(r => r.Outcome == BookOutcome.SlotTaken).Should().Be(racers - 1);

            // And the datastore agrees: exactly one active appointment on the slot.
            await using var verify = new EmrDbContext(Options());
            var active = await verify.Appointments.CountAsync(a =>
                a.SlotId == slotId && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn));
            active.Should().Be(1);
        }
        finally { await Cleanup(slotId); }
    }

    private static async Task Seed(Guid slotId, Guid providerId, Guid locationId, DateTimeOffset start)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO emr.appointment_slot (slot_id, provider_id, location_id, slot_start, slot_end)
              VALUES ($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(slotId);
        cmd.Parameters.AddWithValue(providerId);
        cmd.Parameters.AddWithValue(locationId);
        cmd.Parameters.AddWithValue(start);
        cmd.Parameters.AddWithValue(start.AddMinutes(15));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup(Guid slotId)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using var delAppt = new NpgsqlCommand("DELETE FROM emr.appointment WHERE slot_id = $1", conn);
        delAppt.Parameters.AddWithValue(slotId);
        await delAppt.ExecuteNonQueryAsync();
        await using var delSlot = new NpgsqlCommand("DELETE FROM emr.appointment_slot WHERE slot_id = $1", conn);
        delSlot.Parameters.AddWithValue(slotId);
        await delSlot.ExecuteNonQueryAsync();
    }
}
