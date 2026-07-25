using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Emr.Tests;

/// <summary>Phase 3.3 queue behavior at the datastore (env-gated <c>EMR_TEST_DB</c>): check-in enqueues a
/// min-necessary ticket, ordering is priority-then-arrival, and cancelling an appointment removes its ticket
/// (queue stays consistent with appointment status). Self-cleans by scope tag.</summary>
public class QueueIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("EMR_TEST_DB");
    private static DbContextOptions<EmrDbContext> Options() =>
        new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [Fact]
    public async Task CheckIn_enqueues_and_order_is_priority_then_arrival()
    {
        if (Db is null) return;
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var a1 = await SeedBooked(provider, location, scope, hoursAhead: 1);
            var a2 = await SeedBooked(provider, location, scope, hoursAhead: 2);

            // a1 checks in first at priority 0; a2 second at priority 5 (higher → should lead).
            await CheckIn(a1, "MRS-1", 0);
            await CheckIn(a2, "MRS-2", 5);

            await using var ctx = new EmrDbContext(Options());
            var tickets = await ctx.Set<QueueTicket>().AsNoTracking()
                .Where(t => t.BeneficiaryId == scope && t.State == QueueTicketState.Waiting).ToListAsync();
            tickets.Should().HaveCount(2);

            var ordered = QueueRules.Ordered(tickets).ToList();
            ordered[0].MemberNo.Should().Be("MRS-2");   // higher priority leads despite later arrival
            ordered[1].MemberNo.Should().Be("MRS-1");

            // The checked-in appointments moved to CheckedIn.
            (await ctx.Appointments.AsNoTracking().CountAsync(a =>
                a.BeneficiaryId == scope && a.Status == AppointmentStatus.CheckedIn)).Should().Be(2);
        }
        finally { await Cleanup(scope); }
    }

    [Fact]
    public async Task Cancelling_a_checked_in_appointment_removes_its_ticket()
    {
        if (Db is null) return;
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var appt = await SeedBooked(provider, location, scope, hoursAhead: 1);
            await CheckIn(appt, "MRS-9", 0);

            await using (var ctx = new EmrDbContext(Options()))
            {
                var svc = new AppointmentTransitionService(ctx);
                var r = await svc.CancelAsync(appt, "left the clinic", ifMatch: null, DateTimeOffset.UtcNow);
                r.Outcome.Should().Be(TransitionOutcome.Ok);
            }

            await using var verify = new EmrDbContext(Options());
            var active = await verify.Set<QueueTicket>().AsNoTracking().CountAsync(t =>
                t.AppointmentId == appt && (t.State == QueueTicketState.Waiting || t.State == QueueTicketState.InConsultation));
            active.Should().Be(0);   // ticket removed
        }
        finally { await Cleanup(scope); }
    }

    private static async Task CheckIn(Guid appointmentId, string memberNo, int priority)
    {
        await using var ctx = new EmrDbContext(Options());
        var svc = new AppointmentTransitionService(ctx);
        var r = await svc.CheckInAsync(appointmentId, memberNo, "Display", priority, ifMatch: null, DateTimeOffset.UtcNow);
        r.Outcome.Should().Be(TransitionOutcome.Ok);
    }

    private static async Task<Guid> SeedBooked(Guid provider, Guid location, Guid scope, int hoursAhead)
    {
        var slotId = Guid.NewGuid();
        var apptId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddHours(hoursAhead);
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using (var s = new NpgsqlCommand(
            @"INSERT INTO emr.appointment_slot (slot_id, provider_id, location_id, doctor_id, slot_start, slot_end)
              VALUES ($1,$2,$3,$4,$5,$6)", conn))
        {
            s.Parameters.AddWithValue(slotId);
            s.Parameters.AddWithValue(provider);
            s.Parameters.AddWithValue(location);
            s.Parameters.AddWithValue(scope);            // doctor_id carries the scope tag
            s.Parameters.AddWithValue(start);
            s.Parameters.AddWithValue(start.AddMinutes(15));
            await s.ExecuteNonQueryAsync();
        }
        await using var a = new NpgsqlCommand(
            @"INSERT INTO emr.appointment
                (appointment_id, beneficiary_id, provider_id, location_id, slot_id, appointment_type, status, scheduled_start, scheduled_end)
              VALUES ($1,$2,$3,$4,$5,'Scheduled','Booked',$6,$7)", conn);
        a.Parameters.AddWithValue(apptId);
        a.Parameters.AddWithValue(scope);                // beneficiary_id carries the scope tag
        a.Parameters.AddWithValue(provider);
        a.Parameters.AddWithValue(location);
        a.Parameters.AddWithValue(slotId);
        a.Parameters.AddWithValue(start);
        a.Parameters.AddWithValue(start.AddMinutes(15));
        await a.ExecuteNonQueryAsync();
        return apptId;
    }

    private static async Task Cleanup(Guid scope)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM emr.appointment_queue WHERE beneficiary_id=$1",
            "DELETE FROM emr.appointment WHERE beneficiary_id=$1",
            "DELETE FROM emr.appointment_slot WHERE doctor_id=$1",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(scope);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
