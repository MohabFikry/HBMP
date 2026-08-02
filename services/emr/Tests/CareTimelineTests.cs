using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// The care-episode timeline (ADR-0031), over HTTP.
///
/// <para>An appointment is the START of an episode, and almost everything the platform then does for that
/// patient descends from it. <c>GET /appointments/{id}/timeline</c> read only
/// <c>emr.appointment_history</c> — a row trigger over the appointment row — so it could answer "booked,
/// rescheduled, checked in" and was structurally incapable of anything after arrival. A desk asking "why is
/// this member still here at four o'clock?" got a history that stopped two hours before the question.</para>
///
/// <para>What is proven here: the two sources are MERGED (a timeline that needed reading in two places is
/// the defect), the steps are in time order, and a step carries no clinical content — this timeline is read
/// by reception and the call centre as well as by clinicians.</para>
/// </summary>
[Collection("emr-db")]
public class CareTimelineTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task The_timeline_merges_appointment_status_with_the_visit_it_started()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, apptId, benId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedAsync(app, encId, apptId, benId);
            using var doctor = app.DoctorClient();

            // A consultation: code a diagnosis, sign the note, close the visit.
            (await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/diagnoses",
                new { icdCode = "J01.0", diagnosisRank = "Primary", clinicalStatus = "Active" }, Web))
                .StatusCode.Should().Be(HttpStatusCode.Created);
            var note = await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/notes",
                new { noteType = "SOAP", assessment = "Acute sinusitis" }, Web);
            var noteId = (await note.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("noteId").GetGuid();
            (await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/notes/{noteId}/sign", new { }, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/complete", new { }, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            using var reception = app.ReceptionClient();
            var timeline = await reception.GetFromJsonAsync<List<JsonElement>>($"/api/v1/appointments/{apptId}/timeline")
                ?? throw new InvalidOperationException("the timeline endpoint answered with no body");
            var steps = timeline.Select(s => s.GetProperty("status").GetString()).ToList();

            // The appointment's own status history — what the endpoint could already answer.
            steps.Should().Contain("CheckedIn");
            steps.Should().Contain("Completed");
            // And the episode it started — what it could not.
            steps.Should().Contain(CareSteps.DiagnosisCoded);
            steps.Should().Contain(CareSteps.NoteSigned);
            steps.Should().Contain(CareSteps.VisitEnded);

            // Newest first, across BOTH sources. Merging two lists and leaving them separately ordered is a
            // timeline that reads as two timelines.
            var times = timeline.Select(s => s.GetProperty("at").GetDateTimeOffset()).ToList();
            times.Should().BeInDescendingOrder();
        }
        finally { await CleanupAsync(app, encId); }
    }

    [SkippableFact]
    public async Task A_step_names_the_act_and_never_its_clinical_content()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, apptId, benId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedAsync(app, encId, apptId, benId);
            using var doctor = app.DoctorClient();

            (await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/diagnoses",
                new { icdCode = "E11.9", diagnosisRank = "Primary", clinicalStatus = "Active" }, Web))
                .StatusCode.Should().Be(HttpStatusCode.Created);
            var note = await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/notes",
                new { noteType = "SOAP", assessment = "Type 2 diabetes, poorly controlled" }, Web);
            var noteId = (await note.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("noteId").GetGuid();
            (await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/notes/{noteId}/sign", new { }, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // RECEPTION reads it. That is the whole reason for the rule: the desk is entitled to know a
            // diagnosis was coded on this visit and is structurally forbidden the diagnosis itself.
            using var reception = app.ReceptionClient();
            var body = await (await reception.GetAsync($"/api/v1/appointments/{apptId}/timeline"))
                .Content.ReadAsStringAsync();

            body.Should().Contain(CareSteps.DiagnosisCoded);
            body.Should().NotContain("E11.9", "the ICD code is clinical content the desk may not read");
            body.Should().NotContain("diabetes", "nor is the assessment text");
        }
        finally { await CleanupAsync(app, encId); }
    }

    /// <summary>A checked-in appointment with an open visit against it, and the VisitStarted step the
    /// encounter endpoint would have written — seeded directly because these tests do not go through
    /// POST /encounters (which needs the member-status gate to pass).</summary>
    private static async Task SeedAsync(EmrApiFactory app, Guid encId, Guid apptId, Guid benId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = EmrApiFactory.Ctx();
        db.Appointments.Add(new Appointment
        {
            AppointmentId = apptId, TenantId = app.Tenant, BeneficiaryId = benId,
            ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
            AppointmentType = AppointmentType.Scheduled, Status = AppointmentStatus.CheckedIn,
            ScheduledStart = now.AddHours(-1), ScheduledEnd = now.AddMinutes(-30),
        });
        db.Encounters.Add(new Encounter
        {
            EncounterId = encId, EncounterNo = $"ENC-CTL-{encId.ToString()[..8]}",
            BeneficiaryId = benId, AppointmentId = apptId, TenantId = app.Tenant,
            Status = EncounterStatus.InProgress, StartedAt = now.AddMinutes(-20),
            CreatedBy = EmrTestAuth.DoctorSub,
        });
        db.CareTimeline.Add(new CareStep
        {
            StepId = Guid.NewGuid(), TenantId = app.Tenant, EncounterId = encId, AppointmentId = apptId,
            BeneficiaryId = benId, Step = CareSteps.VisitStarted, OccurredAt = now.AddMinutes(-20),
            Actor = EmrTestAuth.DoctorSub, Source = CareStepSources.Emr,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(EmrApiFactory app, Guid encId)
    {
        if (EmrApiFactory.Db is null) return;
        await using (var db = EmrApiFactory.Ctx())
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM emr.care_timeline WHERE encounter_id = {0}; " +
                "DELETE FROM emr.diagnosis     WHERE encounter_id = {0}; " +
                "DELETE FROM emr.emr_note      WHERE encounter_id = {0}; " +
                "DELETE FROM emr.vital         WHERE encounter_id = {0};", encId);
        }
        await app.CleanupAsync();
    }
}
