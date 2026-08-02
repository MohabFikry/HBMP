using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// Retracting a coded diagnosis (US-031), over HTTP.
///
/// <para>The encounter workspace lets a doctor add an ICD-10 code to the assessment and take it off again,
/// which is ordinary correction of a mis-keyed code — but "take it off again" on a clinical record has three
/// rules that are easy to write a back door around, and this suite is those three rules:</para>
///
/// <list type="number">
///   <item>the row is FLAGGED, never removed — nothing clinical is hard-deleted here;</item>
///   <item>it stops working the moment the encounter's note is signed, because at that point the assessment
///         is a signed clinical statement and the correction path is an addendum. Without this, a retract
///         endpoint is a way to edit a locked note that <c>SoapNoteRules</c> refuses head-on;</item>
///   <item>only the clinician who RECORDED a code may retract it — a doctor does not silently undo a
///         colleague's clinical judgement on their own encounter.</item>
/// </list>
/// </summary>
[Collection("emr-db")]
public class DiagnosisRetractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task The_recorder_retracts_a_code_and_it_is_flagged_rather_than_deleted()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var encId = Guid.NewGuid();
        try
        {
            await SeedEncounter(app, encId);
            using var doctor = app.DoctorClient();

            var added = await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/diagnoses",
                new { icdCode = "J01.90", diagnosisRank = "Primary", clinicalStatus = "Active" }, Web);
            added.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await added.Content.ReadAsStringAsync());
            var dxId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("diagnosisId").GetGuid();

            var gone = await doctor.DeleteAsync($"/api/v1/encounters/{encId}/diagnoses/{dxId}");
            gone.StatusCode.Should().Be(HttpStatusCode.NoContent);

            await using var db = EmrApiFactory.Ctx();
            var row = await db.Diagnoses.AsNoTracking().SingleAsync(d => d.DiagnosisId == dxId);
            row.IsDeleted.Should().BeTrue("a retracted diagnosis is soft-deleted — the platform hard-deletes no clinical data");

            // And the clinical read no longer offers it, so "flagged" is not a distinction the UI has to make.
            var record = await doctor.GetFromJsonAsync<JsonElement>($"/api/v1/encounters/{encId}/clinical");
            record.GetProperty("diagnoses").GetArrayLength().Should().Be(0);
        }
        finally { await CleanupAsync(app, encId); }
    }

    [SkippableFact]
    public async Task A_signed_note_closes_the_retract_path_and_says_to_use_an_addendum()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var encId = Guid.NewGuid();
        try
        {
            await SeedEncounter(app, encId);
            using var doctor = app.DoctorClient();

            var added = await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/diagnoses",
                new { icdCode = "J01.90", diagnosisRank = "Primary", clinicalStatus = "Active" }, Web);
            var dxId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("diagnosisId").GetGuid();

            var note = await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/notes",
                new { noteType = "SOAP", assessment = "Acute sinusitis" }, Web);
            note.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await note.Content.ReadAsStringAsync());
            var noteId = (await note.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("noteId").GetGuid();
            (await doctor.PostAsJsonAsync($"/api/v1/encounters/{encId}/notes/{noteId}/sign", new { }, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var refused = await doctor.DeleteAsync($"/api/v1/encounters/{encId}/diagnoses/{dxId}");
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("encounter-signed");

            await using var db = EmrApiFactory.Ctx();
            (await db.Diagnoses.AsNoTracking().SingleAsync(d => d.DiagnosisId == dxId)).IsDeleted
                .Should().BeFalse("a refused retract must leave the record exactly as it was");
        }
        finally { await CleanupAsync(app, encId); }
    }

    [SkippableFact]
    public async Task A_clinician_may_not_retract_a_code_someone_else_recorded()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var encId = Guid.NewGuid();
        try
        {
            await SeedEncounter(app, encId);

            // Recorded by someone who is NOT the caller — in a real clinic, the triage nurse coding a
            // presenting complaint on the doctor's own encounter. The doctor may read it and act on it; they
            // may not quietly take it off the record.
            var dxId = Guid.NewGuid();
            await using (var seed = EmrApiFactory.Ctx())
            {
                seed.Diagnoses.Add(new Diagnosis
                {
                    DiagnosisId = dxId, EncounterId = encId, IcdCode = "J01.90", TenantId = app.Tenant,
                    DiagnosisRank = DiagnosisRank.Primary, ClinicalStatus = ClinicalStatus.Active,
                    RecordedBy = "nurse-someone-else", RecordedAt = DateTimeOffset.UtcNow,
                });
                await seed.SaveChangesAsync();
            }

            using var doctor = app.DoctorClient();
            var refused = await doctor.DeleteAsync($"/api/v1/encounters/{encId}/diagnoses/{dxId}");
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("not-recorder");
        }
        finally { await CleanupAsync(app, encId); }
    }

    /// <summary>An encounter OWNED by the test doctor — the treating relationship this service resolves from
    /// <c>created_by</c>, which is what gets the caller past the clinical gate before any of the above applies.</summary>
    private static async Task SeedEncounter(EmrApiFactory app, Guid encId)
    {
        await using var db = EmrApiFactory.Ctx();
        db.Encounters.Add(new Encounter
        {
            EncounterId = encId, EncounterNo = $"ENC-DXT-{encId.ToString()[..8]}",
            BeneficiaryId = Guid.NewGuid(), TenantId = app.Tenant,
            Status = EncounterStatus.InProgress, StartedAt = DateTimeOffset.UtcNow,
            CreatedBy = EmrTestAuth.DoctorSub,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The factory's own cleanup deletes encounters, which the clinical children reference — so the
    /// children go first, or the tenant sweep fails on a foreign key and every later test in the collection
    /// inherits the rows this one left behind.</summary>
    private static async Task CleanupAsync(EmrApiFactory app, Guid encId)
    {
        if (EmrApiFactory.Db is null) return;
        await using (var db = EmrApiFactory.Ctx())
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM emr.diagnosis WHERE encounter_id = {0}; " +
                "DELETE FROM emr.emr_note WHERE encounter_id = {0}; " +
                "DELETE FROM emr.vital WHERE encounter_id = {0};", encId);
        }
        await app.CleanupAsync();
    }
}
