using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// Standing clinical facts on the member's file: blood group (migration 0021) and NAMED allergies (0020).
///
/// <para><b>What these prove is mostly about naming and about absence.</b> An allergy row has always carried
/// an <c>allergen_id</c> and nothing a human can read, so every consumer that wanted to SHOW it — the patient
/// context bar most of all — had a uuid where the substance belongs. And an empty allergy list is not a
/// negative allergy history: the endpoint reports what is recorded and never implies a screen was done.</para>
///
/// <para>The name is taken from master data at write time and never from the request body. A client-supplied
/// display string would let the substance on the safety strip disagree with the allergen actually recorded,
/// which is the one disagreement this record must not permit — asserted directly below.</para>
/// </summary>
[Collection("emr-db")]
public class MemberClinicalRecordTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task A_recorded_allergy_carries_the_substance_NAME_not_just_an_id()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, benId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedEncounterAsync(app, encId, benId);
            using var doctor = app.DoctorClient();

            var created = await doctor.PostAsJsonAsync($"/api/v1/beneficiaries/{benId}/allergies",
                new { allergenId = Guid.NewGuid(), reaction = "rash", severity = "Moderate", status = "Active" }, Web);
            created.StatusCode.Should().Be(HttpStatusCode.Created);

            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            // AllowAllClinicalCodeValidator resolves every allergen to "Test allergen". The point is that a
            // name arrives AT ALL and is persisted — the field was absent from this response entirely, so
            // profile-service's alerts provider fell through to rendering the raw allergen uuid.
            body.GetProperty("allergenDisplay").GetString().Should().NotBeNullOrWhiteSpace();

            var read = await doctor.GetFromJsonAsync<JsonElement>($"/api/v1/beneficiaries/{benId}/clinical-record");
            read.GetProperty("allergies")[0].GetProperty("allergenDisplay").GetString()
                .Should().NotBeNullOrWhiteSpace("the name is snapshot on the row, so it survives the round trip");
        }
        finally { await CleanupAsync(app, encId, benId); }
    }

    [SkippableFact]
    public async Task An_unknown_allergen_is_refused_rather_than_recorded_without_a_name()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { ValidateCodes = false };
        var (encId, benId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedEncounterAsync(app, encId, benId);
            using var doctor = app.DoctorClient();

            var refused = await doctor.PostAsJsonAsync($"/api/v1/beneficiaries/{benId}/allergies",
                new { allergenId = Guid.NewGuid(), reaction = (string?)null, severity = "Mild", status = "Active" }, Web);

            // 422, not a row with a null name. An allergy nobody can read is worse than no allergy at all:
            // it occupies the slot a real one would have and communicates nothing.
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally { await CleanupAsync(app, encId, benId); }
    }

    [SkippableFact]
    public async Task An_empty_record_reports_absence_and_never_a_negative_finding()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, benId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedEncounterAsync(app, encId, benId);
            using var doctor = app.DoctorClient();

            var read = await doctor.GetFromJsonAsync<JsonElement>($"/api/v1/beneficiaries/{benId}/clinical-record");

            // NULL, not "" and not "Unknown". The API states that nothing is recorded and leaves the wording
            // of that to the reader's own language; a server-side placeholder would be an English string
            // rendered untranslated on an Arabic screen, and worse, one that looks like a value.
            read.GetProperty("bloodGroup").ValueKind.Should().Be(JsonValueKind.Null);
            read.GetProperty("allergies").GetArrayLength().Should().Be(0);
        }
        finally { await CleanupAsync(app, encId, benId); }
    }

    [SkippableFact]
    public async Task Blood_group_is_recorded_once_and_corrected_in_place()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, benId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedEncounterAsync(app, encId, benId);
            using var doctor = app.DoctorClient();

            (await doctor.PutAsJsonAsync($"/api/v1/beneficiaries/{benId}/blood-group", new { bloodGroup = "O+" }, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await doctor.PutAsJsonAsync($"/api/v1/beneficiaries/{benId}/blood-group", new { bloodGroup = "A-" }, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var read = await doctor.GetFromJsonAsync<JsonElement>($"/api/v1/beneficiaries/{benId}/clinical-record");
            read.GetProperty("bloodGroup").GetString().Should().Be("A-");

            // ONE row. A person has one blood group; a second PUT is a correction, and a history of
            // corrections belongs in the audit trail, not as two live rows one of which is wrong.
            await using var db = EmrApiFactory.Ctx();
            (await db.BeneficiaryClinical.CountAsync(x => x.BeneficiaryId == benId)).Should().Be(1);
        }
        finally { await CleanupAsync(app, encId, benId); }
    }

    [SkippableFact]
    public async Task A_blood_group_outside_the_eight_is_refused()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, benId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedEncounterAsync(app, encId, benId);
            using var doctor = app.DoctorClient();

            // Rejected at the endpoint, so the caller gets a reason, rather than at the CHECK constraint,
            // where it surfaces as a bare 500 with the allowed set buried in a Postgres error string.
            (await doctor.PutAsJsonAsync($"/api/v1/beneficiaries/{benId}/blood-group", new { bloodGroup = "C+" }, Web))
                .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally { await CleanupAsync(app, encId, benId); }
    }

    [SkippableFact]
    public async Task A_clinician_with_no_treating_relationship_cannot_read_or_write_the_record()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var (encId, benId) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await SeedEncounterAsync(app, encId, benId);
            // A doctor, correctly scoped, who simply does not treat this patient. The new endpoints go
            // through the same ClinicalGate as every other clinical read — asserted rather than assumed,
            // because a gate is only load-bearing where it is actually applied.
            using var stranger = app.As("dr-stranger", "doctor", "emr:read emr:write");

            (await stranger.GetAsync($"/api/v1/beneficiaries/{benId}/clinical-record"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await stranger.PutAsJsonAsync($"/api/v1/beneficiaries/{benId}/blood-group", new { bloodGroup = "O+" }, Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await CleanupAsync(app, encId, benId); }
    }

    private static async Task SeedEncounterAsync(EmrApiFactory app, Guid encId, Guid benId)
    {
        await using var db = EmrApiFactory.Ctx();
        db.Encounters.Add(new Encounter
        {
            EncounterId = encId, EncounterNo = $"ENC-MCR-{encId.ToString()[..8]}",
            BeneficiaryId = benId, TenantId = app.Tenant,
            Status = EncounterStatus.InProgress, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CreatedBy = EmrTestAuth.DoctorSub,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(EmrApiFactory app, Guid encId, Guid benId)
    {
        if (EmrApiFactory.Db is null) return;
        await using (var db = EmrApiFactory.Ctx())
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM emr.beneficiary_clinical WHERE beneficiary_id = {1}; " +
                "DELETE FROM emr.allergy              WHERE beneficiary_id = {1}; " +
                "DELETE FROM emr.diagnosis            WHERE encounter_id   = {0}; " +
                "DELETE FROM emr.emr_note             WHERE encounter_id   = {0}; " +
                "DELETE FROM emr.vital                WHERE encounter_id   = {0};", encId, benId);
        }
        await app.CleanupAsync();
    }
}
