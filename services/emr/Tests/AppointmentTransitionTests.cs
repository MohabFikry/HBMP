using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Emr.Tests;

/// <summary>Phase 3.2 transitions proven at the datastore (env-gated <c>EMR_TEST_DB</c>): reschedule frees the
/// old slot and holds the new atomically, cancel/no-show release the slot and promote the waitlist, no-show
/// is guarded, illegal transitions are refused, and If-Match mismatches fail the precondition. Each test
/// self-cleans the rows it seeds.</summary>
public class AppointmentTransitionTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("EMR_TEST_DB");
    private static DbContextOptions<EmrDbContext> Options() =>
        new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [SkippableFact]
    public async Task Reschedule_frees_old_slot_and_holds_new_atomically()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        var oldSlot = await SeedSlot(provider, location, scope, hoursAhead: 24);
        var newSlot = await SeedSlot(provider, location, scope, hoursAhead: 48);
        try
        {
            var appt = await SeedAppointment(provider, location, oldSlot, AppointmentStatus.Booked, scope);

            await using var ctx = new EmrDbContext(Options());
            var svc = new AppointmentTransitionService(ctx);
            var r = await svc.RescheduleAsync(appt, newSlot, ifMatch: null, DateTimeOffset.UtcNow);

            r.Outcome.Should().Be(TransitionOutcome.Ok);
            r.Appointment!.SlotId.Should().Be(newSlot);

            // Old slot is now bookable (no active hold); new slot has exactly one.
            (await ActiveOnSlot(oldSlot)).Should().Be(0);
            (await ActiveOnSlot(newSlot)).Should().Be(1);
        }
        finally { await CleanupScope(scope); }
    }

    [SkippableFact]
    public async Task Cancel_releases_slot_and_promotes_waitlist()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        var slot = await SeedSlot(provider, location, scope, hoursAhead: 24);
        try
        {
            var appt = await SeedAppointment(provider, location, slot, AppointmentStatus.Booked, scope);
            var wl = await SeedWaitlist(provider, location, scope);

            await using var ctx = new EmrDbContext(Options());
            var svc = new AppointmentTransitionService(ctx);
            var r = await svc.CancelAsync(appt, "patient requested", ifMatch: null, DateTimeOffset.UtcNow);

            r.Outcome.Should().Be(TransitionOutcome.Ok);
            r.Appointment!.Status.Should().Be(AppointmentStatus.Cancelled);
            (await ActiveOnSlot(slot)).Should().Be(0);
            r.Promoted!.WaitlistId.Should().Be(wl);
            (await WaitlistStatusOf(wl)).Should().Be("Promoted");
        }
        finally { await CleanupScope(scope); }
    }

    [SkippableFact]
    public async Task NoShow_guarded_then_sets_flag_and_promotes()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        // Slot in the PAST so the no-show window has elapsed.
        var slot = await SeedSlot(provider, location, scope, hoursAhead: -2);
        try
        {
            var appt = await SeedAppointment(provider, location, slot, AppointmentStatus.Booked, scope);
            var wl = await SeedWaitlist(provider, location, scope);

            await using var ctx = new EmrDbContext(Options());
            var svc = new AppointmentTransitionService(ctx);
            var r = await svc.NoShowAsync(appt, ifMatch: null, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));

            r.Outcome.Should().Be(TransitionOutcome.Ok);
            r.Appointment!.Status.Should().Be(AppointmentStatus.NoShow);
            r.Appointment!.NoShow.Should().BeTrue();              // reporting flag
            r.NoShowCount.Should().BeGreaterThanOrEqualTo(1);
            (await ActiveOnSlot(slot)).Should().Be(0);            // freed for backfill
            (await WaitlistStatusOf(wl)).Should().Be("Promoted");
        }
        finally { await CleanupScope(scope); }
    }

    [SkippableFact]
    public async Task NoShow_rejected_before_window_and_when_checked_in()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        var future = await SeedSlot(provider, location, scope, hoursAhead: 24);
        var past = await SeedSlot(provider, location, scope, hoursAhead: -2);
        try
        {
            var futureAppt = await SeedAppointment(provider, location, future, AppointmentStatus.Booked, scope);
            var checkedIn = await SeedAppointment(provider, location, past, AppointmentStatus.CheckedIn, scope);

            await using var ctx = new EmrDbContext(Options());
            var svc = new AppointmentTransitionService(ctx);

            (await svc.NoShowAsync(futureAppt, null, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)))
                .Outcome.Should().Be(TransitionOutcome.IllegalTransition);   // window not passed
            (await svc.NoShowAsync(checkedIn, null, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)))
                .Outcome.Should().Be(TransitionOutcome.IllegalTransition);   // already checked in
        }
        finally { await CleanupScope(scope); }
    }

    [SkippableFact]
    public async Task Cancel_on_completed_is_illegal()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        var slot = await SeedSlot(provider, location, scope, hoursAhead: 24);
        try
        {
            var appt = await SeedAppointment(provider, location, slot, AppointmentStatus.Completed, scope);
            await using var ctx = new EmrDbContext(Options());
            var svc = new AppointmentTransitionService(ctx);
            (await svc.CancelAsync(appt, "x", null, DateTimeOffset.UtcNow)).Outcome
                .Should().Be(TransitionOutcome.IllegalTransition);
        }
        finally { await CleanupScope(scope); }
    }

    [SkippableFact]
    public async Task Stale_IfMatch_fails_precondition()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        var slot = await SeedSlot(provider, location, scope, hoursAhead: 24);
        try
        {
            var appt = await SeedAppointment(provider, location, slot, AppointmentStatus.Booked, scope);
            await using var ctx = new EmrDbContext(Options());
            var svc = new AppointmentTransitionService(ctx);
            // A wildly wrong xmin can never match the row's real version → 412.
            var r = await svc.CancelAsync(appt, "x", ifMatch: 1u, DateTimeOffset.UtcNow);
            r.Outcome.Should().Be(TransitionOutcome.PreconditionFailed);
        }
        finally { await CleanupScope(scope); }
    }

    [SkippableFact]
    public async Task Idempotency_store_replays_a_seen_key()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var key = $"idem-{Guid.NewGuid()}";
        var apptId = Guid.NewGuid();
        try
        {
            await using var ctx = new EmrDbContext(Options());
            var store = new IdempotencyStore(ctx, TimeProvider.System);
            (await store.FindAsync(key)).Should().BeNull();
            await store.RecordAsync(key, "cancel", apptId, 200);
            var found = await store.FindAsync(key);
            found!.Operation.Should().Be("cancel");
            found.AppointmentId.Should().Be(apptId);
        }
        finally
        {
            await using var conn = new NpgsqlConnection(Db);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM emr.processed_request WHERE idempotency_key = $1", conn);
            cmd.Parameters.AddWithValue(key);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---- seed / assert helpers (tag rows with a scope guid in beneficiary_id space for cleanup) ----

    /// <param name="doctor">The slot's practitioner. Defaults to <paramref name="scope"/>, which is also the
    /// cleanup tag — pass one explicitly only alongside a provider/location the cleanup below also removes.</param>
    private static async Task<Guid> SeedSlot(Guid provider, Guid location, Guid scope, int hoursAhead, Guid? doctor = null)
    {
        var slotId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddHours(hoursAhead);
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO emr.appointment_slot (slot_id, provider_id, location_id, doctor_id, slot_start, slot_end)
              VALUES ($1,$2,$3,$4,$5,$6)", conn);
        cmd.Parameters.AddWithValue(slotId);
        cmd.Parameters.AddWithValue(provider);
        cmd.Parameters.AddWithValue(location);
        cmd.Parameters.AddWithValue(doctor ?? scope);    // doctor_id carries the scope tag for cleanup
        cmd.Parameters.AddWithValue(start);
        cmd.Parameters.AddWithValue(start.AddMinutes(15));
        await cmd.ExecuteNonQueryAsync();
        return slotId;
    }

    private static async Task<Guid> SeedAppointment(Guid provider, Guid location, Guid slot, AppointmentStatus status, Guid scope)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO emr.appointment
                (appointment_id, beneficiary_id, provider_id, location_id, slot_id, appointment_type, status,
                 scheduled_start, scheduled_end)
              SELECT $1,$2,$3,$4,$5,'Scheduled',$6, slot_start, slot_end FROM emr.appointment_slot WHERE slot_id=$5", conn);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(scope);              // beneficiary_id carries the scope tag
        cmd.Parameters.AddWithValue(provider);
        cmd.Parameters.AddWithValue(location);
        cmd.Parameters.AddWithValue(slot);
        cmd.Parameters.AddWithValue(status.ToString());
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    // ── 18.A3 — waitlist promotion must be locked, and cancel must be one transaction ─────────────

    [SkippableFact]
    public async Task Two_concurrent_cancels_promote_two_distinct_waitlist_entries()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        var slotA = await SeedSlot(provider, location, scope, hoursAhead: 24);
        var slotB = await SeedSlot(provider, location, scope, hoursAhead: 48);
        try
        {
            var apptA = await SeedAppointment(provider, location, slotA, AppointmentStatus.Booked, scope);
            var apptB = await SeedAppointment(provider, location, slotB, AppointmentStatus.Booked, scope);
            var wl1 = await SeedWaitlist(provider, location, scope);
            var wl2 = await SeedWaitlist(provider, location, scope);

            // Two slots free at once. Unlocked, both cancels read the SAME head of the waitlist and
            // promoted it twice — one freed slot, two people told they had it, and the second waiting
            // beneficiary silently skipped. FOR UPDATE SKIP LOCKED makes the second cancel take the NEXT entry.
            var results = await Task.WhenAll(new[] { apptA, apptB }.Select(async id =>
            {
                await using var ctx = new EmrDbContext(Options());
                return await new AppointmentTransitionService(ctx)
                    .CancelAsync(id, "clinic closed", ifMatch: null, DateTimeOffset.UtcNow);
            }));

            results.Should().OnlyContain(r => r.Outcome == TransitionOutcome.Ok);
            var promoted = results.Select(r => r.Promoted!.WaitlistId).ToList();
            promoted.Should().HaveCount(2);
            promoted.Should().OnlyHaveUniqueItems("one freed slot may promote only one waitlist entry");
            promoted.Should().BeEquivalentTo(new[] { wl1, wl2 });

            (await WaitlistStatusOf(wl1)).Should().Be("Promoted");
            (await WaitlistStatusOf(wl2)).Should().Be("Promoted");
        }
        finally { await CleanupScope(scope); }
    }

    [SkippableFact]
    public async Task A_cancel_with_a_stale_If_Match_leaves_the_waitlist_untouched()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        var slot = await SeedSlot(provider, location, scope, hoursAhead: 24);
        try
        {
            var appt = await SeedAppointment(provider, location, slot, AppointmentStatus.Booked, scope);
            var wl = await SeedWaitlist(provider, location, scope);

            await using var ctx = new EmrDbContext(Options());
            var r = await new AppointmentTransitionService(ctx)
                .CancelAsync(appt, "typo", ifMatch: 1u, DateTimeOffset.UtcNow);   // stale xmin

            r.Outcome.Should().Be(TransitionOutcome.PreconditionFailed);
            // Cancel is ONE transaction (18.A3): a failed status change cannot leave a promoted waitlist
            // entry behind. This used to be three unwrapped SaveChanges.
            (await WaitlistStatusOf(wl)).Should().Be("Waitlisted");
        }
        finally { await CleanupScope(scope); }
    }

    private static async Task<Guid> SeedWaitlist(Guid provider, Guid location, Guid scope)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO emr.waitlist_entry (waitlist_id, beneficiary_id, provider_id, location_id, appointment_type)
              VALUES ($1,$2,$3,$4,'Scheduled')", conn);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(scope);
        cmd.Parameters.AddWithValue(provider);
        cmd.Parameters.AddWithValue(location);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<int> ActiveOnSlot(Guid slot)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM emr.appointment WHERE slot_id=$1 AND status IN ('Booked','CheckedIn')", conn);
        cmd.Parameters.AddWithValue(slot);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string> WaitlistStatusOf(Guid id)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT status FROM emr.waitlist_entry WHERE waitlist_id=$1", conn);
        cmd.Parameters.AddWithValue(id);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task CleanupScope(Guid scope)
    {
        await using var conn = new NpgsqlConnection(Db);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM emr.appointment WHERE beneficiary_id=$1",
            "DELETE FROM emr.waitlist_entry WHERE beneficiary_id=$1",
            "DELETE FROM emr.appointment_slot WHERE doctor_id=$1",
            // A slot seeded under a DIFFERENT doctor (the doctor-change test) is not tagged by the scope, so
            // it is removed by the appointment that pointed at it having already gone — the provider is
            // unique per test, which is what makes this safe.
            "DELETE FROM emr.appointment_slot WHERE provider_id IN (SELECT provider_id FROM emr.appointment_slot WHERE doctor_id=$1)",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(scope);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Rescheduling_onto_another_doctors_slot_moves_the_DOCTOR_too()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var scope = Guid.NewGuid();
        var otherDoctor = Guid.NewGuid();
        var (provider, location) = (Guid.NewGuid(), Guid.NewGuid());
        var oldSlot = await SeedSlot(provider, location, scope, hoursAhead: 24);
        var newSlot = await SeedSlot(provider, location, scope, hoursAhead: 48, doctor: otherDoctor);
        try
        {
            var appt = await SeedAppointment(provider, location, oldSlot, AppointmentStatus.Booked, scope);

            await using var ctx = new EmrDbContext(Options());
            var r = await new AppointmentTransitionService(ctx)
                .RescheduleAsync(appt, newSlot, ifMatch: null, DateTimeOffset.UtcNow);

            r.Outcome.Should().Be(TransitionOutcome.Ok);
            // The omission was invisible while the picker only ever offered the appointment's OWN doctor, so
            // the two could not disagree. Once a desk can move a patient to a different practitioner, an
            // appointment left pointing at its old doctor while sitting in a new doctor's slot contradicts
            // itself: the board names one clinician, the session belongs to another, and the patient is
            // called by neither.
            r.Appointment!.DoctorId.Should().Be(otherDoctor);
        }
        finally { await CleanupScope(scope); }
    }
}
