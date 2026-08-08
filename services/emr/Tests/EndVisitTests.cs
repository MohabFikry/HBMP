using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// Ending a visit (23 §6), over HTTP.
///
/// <para><b>This transition existed on paper and nowhere else.</b> <c>EncounterStatus.Completed</c> has been
/// in the enum since phase 1 and <c>AppointmentWorkflow</c> has listed CheckedIn → Completed with the comment
/// "encounter closed (phase 4)" since phase 3 — and no code path ever wrote either value. The visible
/// consequence was on the doctor's day list, which offers "Start visit" for any CheckedIn appointment and so
/// kept offering it for patients who had already been seen and sent home.</para>
///
/// <para>The property under test is that ONE call closes BOTH: leaving the appointment open is the exact
/// state that caused the defect, and a second endpoint for the second half would be a second chance to end
/// up back in it.</para>
/// </summary>
[Collection("emr-db")]
public class EndVisitTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Closing_the_visit_completes_the_encounter_and_its_appointment_together()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, apptId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedAsync(app, encId, apptId);
            using var doctor = app.DoctorClient();

            var closed = await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/complete", new { }, Web);
            closed.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await closed.Content.ReadAsStringAsync());

            await using var db = EmrApiFactory.Ctx();
            var enc = await db.Encounters.AsNoTracking().SingleAsync(e => e.EncounterId == encId);
            enc.Status.Should().Be(EncounterStatus.Completed);
            enc.EndedAt.Should().NotBeNull("a closed visit with no end time has no duration");
            enc.EndedBy.Should().Be(EmrTestAuth.DoctorSub);

            var appt = await db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentId == apptId);
            appt.Status.Should().Be(AppointmentStatus.Completed,
                "the appointment is what the day board reads — closing only the encounter is the defect this fixes");

            // The patient is no longer sitting on anyone's worklist.
            var queue = await db.QueueEntries.AsNoTracking().SingleAsync(q => q.EncounterId == encId);
            queue.State.Should().Be(QueueState.Done);
        }
        finally { await CleanupAsync(app, encId); }
    }

    [SkippableFact]
    public async Task Closing_a_closed_visit_answers_with_the_visit_rather_than_a_conflict()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, apptId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedAsync(app, encId, apptId);
            using var doctor = app.DoctorClient();

            (await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/complete", new { }, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            // "Save & finalize" saves, signs and closes in sequence. A retry after a partial failure must not
            // become an error the doctor has no way to act on.
            (await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/complete", new { }, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            await using var db = EmrApiFactory.Ctx();
            (await db.Encounters.AsNoTracking().SingleAsync(e => e.EncounterId == encId))
                .Status.Should().Be(EncounterStatus.Completed);
        }
        finally { await CleanupAsync(app, encId); }
    }

    [SkippableFact]
    public async Task A_clinician_may_not_close_a_visit_they_did_not_open()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, apptId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            // Opened by someone else. A colleague may READ this record; ending another clinician's
            // consultation from under them is a different act, and the same reasoning that lets only a
            // note's author sign it applies here.
            await SeedAsync(app, encId, apptId, createdBy: Guid.NewGuid().ToString());
            using var doctor = app.DoctorClient();

            var refused = await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/complete", new { }, Web);
            // The treating gate answers first — the caller does not own this encounter, so they are not
            // treating this patient. Either way the visit stays open, which is what matters.
            refused.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

            await using var db = EmrApiFactory.Ctx();
            (await db.Encounters.AsNoTracking().SingleAsync(e => e.EncounterId == encId))
                .Status.Should().Be(EncounterStatus.InProgress);
            (await db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentId == apptId))
                .Status.Should().Be(AppointmentStatus.CheckedIn);
        }
        finally { await CleanupAsync(app, encId); }
    }

    /// <summary>A checked-in appointment with an open visit against it — the state the day list shows as
    /// "Start visit" and the state a finished consultation was stuck in.</summary>
    private static async Task SeedAsync(EmrApiFactory app, Guid encId, Guid apptId, string? createdBy = null)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = EmrApiFactory.Ctx();
        db.Appointments.Add(new Appointment
        {
            AppointmentId = apptId, TenantId = app.Tenant, BeneficiaryId = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
            AppointmentType = AppointmentType.Scheduled, Status = AppointmentStatus.CheckedIn,
            ScheduledStart = now.AddHours(-1), ScheduledEnd = now.AddMinutes(-30),
        });
        db.Encounters.Add(new Encounter
        {
            EncounterId = encId, EncounterNo = $"ENC-EVT-{encId.ToString()[..8]}",
            BeneficiaryId = Guid.NewGuid(), AppointmentId = apptId, TenantId = app.Tenant,
            Status = EncounterStatus.InProgress, StartedAt = now.AddMinutes(-20),
            CreatedBy = createdBy ?? EmrTestAuth.DoctorSub,
        });
        db.QueueEntries.Add(new QueueEntry
        {
            QueueEntryId = Guid.NewGuid(), EncounterId = encId, BeneficiaryId = Guid.NewGuid(),
            TenantId = app.Tenant, State = QueueState.Waiting, EnqueuedAt = now.AddMinutes(-20),
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(EmrApiFactory app, Guid encId)
    {
        if (EmrApiFactory.Db is null) return;
        await using (var db = EmrApiFactory.Ctx())
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM emr.diagnosis WHERE encounter_id = {0}; " +
                "DELETE FROM emr.emr_note  WHERE encounter_id = {0}; " +
                "DELETE FROM emr.vital     WHERE encounter_id = {0};", encId);
        }
        await app.CleanupAsync();
    }
}
