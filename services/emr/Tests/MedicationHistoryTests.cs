using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// 32.2 — the medication list a patient is already on.
/// </summary>
/// <remarks>
/// <para>
/// This table has existed since phase 4.1 with a POST that nothing ever called — not the SPA, not another
/// service. It fed <c>/clinical</c>'s medication list and the FHIR <c>MedicationStatement</c> projection with
/// nothing at all, so both reported "no medications" as a fact about every patient on the platform.
/// </para>
/// <para>
/// It is also half of the prescribing interaction check's input (32.1), and the half that cannot be derived
/// from anywhere else: <c>MedicationSource.SelfReported</c> and <c>.External</c> exist precisely to record
/// medicines Mersal did not prescribe. So the read matters as much as the write, and stopping one matters as
/// much as recording it — a medicine the patient stopped taking must leave the interaction input, or the
/// check starts warning about a drug nobody is on.
/// </para>
/// </remarks>
[Collection("emr-db")]
public class MedicationHistoryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task A_recorded_medication_comes_back_on_the_read()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var beneficiary = Guid.NewGuid();
        try
        {
            await SeedTreatingRelationship(app, beneficiary);
            using var doctor = app.DoctorClient();
            var created = await Record(doctor, beneficiary, "SelfReported");

            var rows = await Read(doctor, beneficiary);

            rows.Should().ContainSingle(r => r.MedHistoryId == created && r.Source == "SelfReported"
                                             && r.Status == "Active");
        }
        finally { await CleanAsync(beneficiary); await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Stopping_a_medication_takes_it_out_of_the_active_list_without_deleting_it()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var beneficiary = Guid.NewGuid();
        try
        {
            await SeedTreatingRelationship(app, beneficiary);
            using var doctor = app.DoctorClient();
            var id = await Record(doctor, beneficiary, "External");

            var stop = await doctor.PostAsJsonAsync(
                $"/api/v1/beneficiaries/{beneficiary}/medication-history/{id}/stop",
                new { endDate = new DateOnly(2026, 8, 20) });
            stop.StatusCode.Should().Be(HttpStatusCode.OK);

            var active = await Read(doctor, beneficiary, status: "Active");
            active.Should().NotContain(r => r.MedHistoryId == id,
                "a stopped medicine must leave the interaction check's input");

            // Never deleted: what a patient WAS taking is part of the clinical picture.
            var all = await Read(doctor, beneficiary);
            all.Should().ContainSingle(r => r.MedHistoryId == id && r.Status == "Stopped");
        }
        finally { await CleanAsync(beneficiary); await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Stopping_one_that_is_already_stopped_is_refused_rather_than_silently_restamped()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var beneficiary = Guid.NewGuid();
        try
        {
            await SeedTreatingRelationship(app, beneficiary);
            using var doctor = app.DoctorClient();
            var id = await Record(doctor, beneficiary, "SelfReported");
            await doctor.PostAsJsonAsync($"/api/v1/beneficiaries/{beneficiary}/medication-history/{id}/stop",
                new { endDate = new DateOnly(2026, 8, 1) });

            var again = await doctor.PostAsJsonAsync(
                $"/api/v1/beneficiaries/{beneficiary}/medication-history/{id}/stop",
                new { endDate = new DateOnly(2026, 8, 20) });

            again.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "overwriting the end date would move a recorded clinical fact with nothing saying it moved");
        }
        finally { await CleanAsync(beneficiary); await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_unparseable_status_filter_narrows_nothing()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var beneficiary = Guid.NewGuid();
        try
        {
            await SeedTreatingRelationship(app, beneficiary);
            using var doctor = app.DoctorClient();
            await Record(doctor, beneficiary, "Prescribed");

            var rows = await Read(doctor, beneficiary, status: "nonsense");

            rows.Should().NotBeEmpty(
                "an empty list would read as 'this patient takes nothing', which is the false negative the "
                + "whole feature exists to remove");
        }
        finally { await CleanAsync(beneficiary); await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Reception_cannot_read_a_medication_list()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        var beneficiary = Guid.NewGuid();
        try
        {
            using var reception = app.ReceptionClient();

            var resp = await reception.GetAsync($"/api/v1/beneficiaries/{beneficiary}/medication-history");

            resp.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        }
        finally { await CleanAsync(beneficiary); await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- harness

    private sealed record Row(Guid MedHistoryId, Guid DrugId, string Source, string Status);

    /// <summary>
    /// An in-progress encounter, so the caller actually treats this patient.
    /// </summary>
    /// <remarks>
    /// ClinicalGate refuses <c>emr:write</c> on a beneficiary the caller has no treating relationship with,
    /// which is US-030 working. A fixture that invented a beneficiary and expected a doctor to write to it
    /// was testing a request the platform is right to refuse — the first run of this suite 403'd on exactly
    /// that, which is the gate proving itself rather than a defect.
    /// </remarks>
    private static async Task SeedTreatingRelationship(EmrApiFactory app, Guid beneficiary)
    {
        await using var db = EmrApiFactory.Ctx();
        db.Encounters.Add(new Encounter
        {
            EncounterId = Guid.NewGuid(),
            EncounterNo = $"ENC-MEDS-{Guid.NewGuid().ToString()[..8]}",
            BeneficiaryId = beneficiary,
            TenantId = app.Tenant,
            Status = EncounterStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            CreatedBy = EmrTestAuth.DoctorSub,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> Record(HttpClient client, Guid beneficiary, string source)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/beneficiaries/{beneficiary}/medication-history",
            new { drugId = Guid.NewGuid(), source, startDate = new DateOnly(2026, 1, 1), status = "Active" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<Row>(Web);
        return body!.MedHistoryId;
    }

    private static async Task<IReadOnlyList<Row>> Read(HttpClient client, Guid beneficiary, string? status = null)
    {
        var url = $"/api/v1/beneficiaries/{beneficiary}/medication-history"
                  + (status is null ? "" : $"?status={status}");
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<List<Row>>(Web) ?? [];
    }

    private static async Task CleanAsync(Guid beneficiary)
    {
        if (EmrApiFactory.Db is null) return;
        await using var db = EmrApiFactory.Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM emr.medication_history WHERE beneficiary_id = {0}", beneficiary);
    }
}
