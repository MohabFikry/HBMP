using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Api;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// Step 2 is authoritative and never trusts step 1 (phase 26.4; doc 43 §5, §8 invariant 4).
/// </summary>
/// <remarks>
/// Step 1 runs in the doctor's browser and is advisory. If its verdict were an input to submission, a
/// crafted payload carrying "validated: true" would walk past the entire engine — the same class of hole as
/// trusting a client-filtered payload. So the server re-evaluates from scratch on submit and reads nothing
/// the client claims about the outcome.
/// </remarks>
[Collection("prescribing-api")]
public class ForgedClientVerdictTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task A_FORGED_CLEAN_PAYLOAD_IS_STILL_REFUSED_FOR_A_DRUG_THE_ENGINE_WARNS_ON()
    {
        // The registry-pinned test. The client asserts everything is fine — no acknowledgements, and for
        // good measure the legacy `acknowledgeAlerts` flag set true — for a pair the interaction list knows
        // about. The server must re-derive the warning itself and refuse.
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            app.Ports.Interactions.Add(new InteractionFact(
                app.DrugA, app.DrugB, ClinicalSeverity.Major, "Additive toxicity"));

            using var client = app.Prescriber();
            var response = await Submit(client, app, acknowledgements: []);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
                "the server re-validated and found the interaction the client omitted");

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
            problem.GetProperty("title").GetString().Should().Be("unacknowledged-warning");
            problem.GetProperty("detail").GetString().Should().Contain("Interaction");

            await using var db = PrescribingApiFactory.Ctx();
            (await db.Prescriptions.CountAsync(p => p.BeneficiaryId == app.Beneficiary))
                .Should().Be(0, "a refused submission writes no prescription");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_BENEFIT_REFUSAL_CANNOT_BE_ACKNOWLEDGED_AWAY()
    {
        // Benefit rules block; clinical checks warn. An acknowledgement is the override mechanism for the
        // second and must not work on the first, or "blocked" means nothing.
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var lineA = Guid.NewGuid();
            app.Ports.BenefitOutcomes.Add(new BenefitOutcome(
                lineA, BenefitState.Blocked, "Outside the UNHCR formulary.", "خارج قائمة الأدوية."));

            using var client = app.Prescriber();
            var response = await Submit(client, app, lineAId: lineA, acknowledgements:
            [
                new LineAcknowledgement(lineA, "Benefit", "I would like to prescribe it anyway"),
            ]);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
            problem.GetProperty("title").GetString().Should().Be("blocked-by-benefit-rule");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_acknowledged_warning_is_accepted_and_the_REASON_is_stored()
    {
        // The other half of the rule: overrides are expected and recorded, not prevented. Blocking a doctor
        // on advice of uncertain provenance would be the greater harm (doc 43 D1).
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var lineA = Guid.NewGuid();
            var lineB = Guid.NewGuid();
            app.Ports.Interactions.Add(new InteractionFact(
                app.DrugA, app.DrugB, ClinicalSeverity.Major, "Additive toxicity"));

            using var client = app.Prescriber();
            var response = await Submit(client, app, lineAId: lineA, lineBId: lineB, acknowledgements:
            [
                new LineAcknowledgement(lineA, "Interaction", "Monitoring renal function weekly"),
                new LineAcknowledgement(lineB, "Interaction", "Monitoring renal function weekly"),
            ]);

            response.StatusCode.Should().Be(HttpStatusCode.Created);

            await using var db = PrescribingApiFactory.Ctx();
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .SingleAsync(p => p.BeneficiaryId == app.Beneficiary);

            var overrides = await db.PrescriptionLineOverrides.AsNoTracking()
                .Where(o => o.PrescriptionId == rx.PrescriptionId).ToListAsync();

            overrides.Should().HaveCount(2);
            overrides.Should().OnlyContain(o => o.Reason == "Monitoring renal function weekly");
            overrides.Should().OnlyContain(o => o.AcknowledgedBy.Length > 0,
                "the approver needs to know who accepted the risk, not only that someone did");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Submission_records_the_SERVERS_run_stamped_Step2()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            using var client = app.Prescriber();
            var response = await Submit(client, app, acknowledgements: []);
            response.StatusCode.Should().Be(HttpStatusCode.Created);

            await using var db = PrescribingApiFactory.Ctx();
            var run = await db.PrescriptionValidations.AsNoTracking()
                .SingleAsync(v => v.BeneficiaryId == app.Beneficiary);

            run.Step.Should().Be("Step2", "what is recorded is what the SERVER concluded");
            run.PrescriptionId.Should().NotBeNull();
            run.EngineVersion.Should().Be(PrescriptionValidationService.EngineVersion,
                "\"why did this not warn?\" needs an answer that survives the engine changing");
            run.Findings.Should().Contain("Indication");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Duration_and_the_diagnosis_snapshot_are_persisted()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            // The ENCOUNTER's diagnoses — which is where the server reads them from since 28.2. The
            // request body still carries a list (see Submit); it is no longer consulted.
            app.Ports.EncounterDiagnoses[app.Encounter] = ["E11.9", "I10"];

            using var client = app.Prescriber();
            var response = await Submit(client, app, acknowledgements: [], diagnoses: ["E11.9", "I10"]);
            response.StatusCode.Should().Be(HttpStatusCode.Created);

            await using var db = PrescribingApiFactory.Ctx();
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .SingleAsync(p => p.BeneficiaryId == app.Beneficiary);

            rx.Lines.Should().OnlyContain(l => l.DurationDays == 7,
                "duration is what makes a daily-dose ceiling checkable at all");
            rx.PrimaryIcdCode.Should().Be("E11.9");
            rx.DiagnosisSnapshot.Should().Contain("E11.9").And.Contain("I10");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_diagnosis_snapshot_does_not_change_when_the_encounter_diagnosis_later_does()
    {
        // A snapshot, not a join. The indication check is a statement about what was known at prescribing
        // time; a correction next week must not rewrite what was actually checked.
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            app.Ports.EncounterDiagnoses[app.Encounter] = ["E11.9"];

            using var client = app.Prescriber();
            (await Submit(client, app, acknowledgements: [], diagnoses: ["E11.9"]))
                .StatusCode.Should().Be(HttpStatusCode.Created);

            await using var db = PrescribingApiFactory.Ctx();
            var before = (await db.Prescriptions.AsNoTracking()
                .SingleAsync(p => p.BeneficiaryId == app.Beneficiary)).DiagnosisSnapshot;

            // The encounter's diagnosis is corrected. Nothing in pharmacy joins to it, so nothing changes —
            // this asserts the absence of a link that a future "improvement" might add.
            app.Ports.Indications[app.DrugA] = ["J01"];

            var after = (await db.Prescriptions.AsNoTracking()
                .SingleAsync(p => p.BeneficiaryId == app.Beneficiary)).DiagnosisSnapshot;

            after.Should().Be(before).And.Contain("E11.9");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Validate_returns_five_state_findings_and_persists_the_run_without_a_prescription()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            using var client = app.Prescriber();
            var lineA = Guid.NewGuid();

            var response = await client.PostAsJsonAsync(
                new Uri("/api/v1/prescriptions/validate", UriKind.Relative),
                new ValidatePrescriptionRequest(
                    app.Beneficiary, app.Encounter,
                    [new CreateRxLine(app.DrugA, "500mg", "PO", "BD", 14, 0, DurationDays: 7, ClientLineId: lineA)],
                    ["E11.9"]),
                Web);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Web);

            body.GetProperty("findings").EnumerateArray().Should().NotBeEmpty();
            body.GetProperty("lineStates").GetProperty(lineA.ToString()).GetString().Should().NotBeNull();
            body.GetProperty("engineVersion").GetString().Should().Be(PrescriptionValidationService.EngineVersion);

            // Every finding carries its provenance, or says plainly that it had none.
            foreach (var f in body.GetProperty("findings").EnumerateArray())
            {
                f.GetProperty("state").GetString().Should().BeOneOf(
                    "Ok", "Warning", "Blocked", "NotChecked", "Unavailable");
                f.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
            }

            await using var db = PrescribingApiFactory.Ctx();
            var run = await db.PrescriptionValidations.AsNoTracking()
                .SingleAsync(v => v.BeneficiaryId == app.Beneficiary);
            run.Step.Should().Be("Step1");
            run.PrescriptionId.Should().BeNull("validating composes nothing; it persists no draft prescription");

            (await db.Prescriptions.CountAsync(p => p.BeneficiaryId == app.Beneficiary)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    /*
     * ================================================================= 28.2 — the trusted-client hole
     *
     * Step 2 re-ran every check server-side and then read the diagnosis list out of the REQUEST BODY
     * (doc 44 §1.3). Everything above proves the server re-derives its own VERDICT; these prove it also
     * sources its own INPUT, which is the half that was missing. A hole in the most important input of the
     * check phase 26 was built to make trustworthy.
     */

    [SkippableFact]
    public async Task A_FORGED_DIAGNOSIS_ARRAY_CHANGES_NOTHING()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            // The encounter records diabetes. The drug is indicated for diabetes. On the truth, the
            // indication check passes and the line submits.
            app.Ports.EncounterDiagnoses[app.Encounter] = ["E11.9"];
            app.Ports.Indications[app.DrugA] = ["E11"];
            app.Ports.Indications[app.DrugB] = ["E11"];

            using var client = app.Prescriber();

            // The client sends a DIFFERENT diagnosis — one the drug is not indicated for. If the server
            // read the body, this would turn a clean line into an off-label warning and the submission
            // would be refused for want of an acknowledgement.
            var response = await Submit(client, app, acknowledgements: [], diagnoses: ["Z00.0"]);

            response.StatusCode.Should().Be(HttpStatusCode.Created,
                "the server reads the encounter, so what the client claimed about the diagnosis is irrelevant");

            await using var db = PrescribingApiFactory.Ctx();
            var rx = await db.Prescriptions.AsNoTracking().SingleAsync(p => p.BeneficiaryId == app.Beneficiary);

            // And what is RECORDED is the encounter's diagnosis, not the forged one — the stored record
            // must not disagree with the findings stored beside it.
            rx.PrimaryIcdCode.Should().Be("E11.9");
            rx.DiagnosisSnapshot.Should().Contain("E11.9").And.NotContain("Z00.0");

            var run = await db.PrescriptionValidations.AsNoTracking()
                .SingleAsync(v => v.BeneficiaryId == app.Beneficiary && v.Step == "Step2");
            run.Findings.Should().NotContain("Z00.0");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_EMPTIED_diagnosis_array_cannot_suppress_a_finding()
    {
        // The other direction, and the cheaper attack: send nothing and let the indication check report
        // "no diagnosis recorded" instead of the off-label warning it owes.
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            app.Ports.EncounterDiagnoses[app.Encounter] = ["E11.9"];
            app.Ports.Indications[app.DrugA] = ["J01"];   // not indicated for diabetes → off-label warning

            using var client = app.Prescriber();
            var response = await Submit(client, app, acknowledgements: [], diagnoses: []);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableContent,
                "the encounter's diagnosis still produces the off-label warning, which is unacknowledged");
            (await response.Content.ReadAsStringAsync()).Should().Contain("Indication");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task When_emr_cannot_be_read_the_indication_check_is_Unavailable_and_never_Ok()
    {
        // "No diagnosis is recorded" and "we could not find out what is recorded" are different statements
        // about different things, and only one of them is the encounter's fault. Falling back to the
        // client's list on an outage would reopen the hole exactly when the server is least able to notice.
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            app.Ports.DiagnosisFetchFailure = "emr did not respond within 5s";

            using var client = app.Prescriber();
            var response = await Submit(client, app, acknowledgements: [], diagnoses: ["E11.9"]);
            response.StatusCode.Should().Be(HttpStatusCode.Created, "an outage does not block prescribing");

            await using var db = PrescribingApiFactory.Ctx();
            var run = await db.PrescriptionValidations.AsNoTracking()
                .SingleAsync(v => v.BeneficiaryId == app.Beneficiary && v.Step == "Step2");

            run.Findings.Should().Contain("Unavailable").And.Contain("diagnoses could not be read");
            run.OverallState.Should().Be("Unavailable");
            // The forged list is not silently substituted for the source that failed.
            run.Findings.Should().NotContain("Listed indication");
        }
        finally { await app.CleanupAsync(); }
    }

    private static async Task<HttpResponseMessage> Submit(
        HttpClient client, PrescribingApiFactory app, IReadOnlyList<LineAcknowledgement> acknowledgements,
        Guid? lineAId = null, Guid? lineBId = null, IReadOnlyList<string>? diagnoses = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/prescriptions", UriKind.Relative))
        {
            Content = JsonContent.Create(new CreatePrescriptionRequest(
                app.Beneficiary, app.Encounter, null,
                // The forged part: the legacy "I acknowledged everything" flag, which must not substitute
                // for a per-warning reason.
                AcknowledgeAlerts: true,
                Lines:
                [
                    new CreateRxLine(app.DrugA, "500mg", "PO", "BD", 14, 0,
                        DurationDays: 7, ClientLineId: lineAId ?? Guid.NewGuid()),
                    new CreateRxLine(app.DrugB, "10mg", "PO", "OD", 7, 0,
                        DurationDays: 7, ClientLineId: lineBId ?? Guid.NewGuid()),
                ],
                DiagnosisIcdCodes: [.. diagnoses ?? ["E11.9"]],
                Acknowledgements: [.. acknowledgements]), options: Web),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }
}
