using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Patient.Api;
using Mersal.Patient.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Patient.Tests;

/// <summary>
/// A retried registration registers one person.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>POST /api/v1/beneficiaries</c> has answered 400 without an
/// <c>Idempotency-Key</c> since phase 3 — and then discarded the header. Nothing stored it; nothing read it.
/// So the required header bought nothing at all, and a retry after a dropped response, a double-submitted
/// form or a client reconnect created a SECOND PERSON.</para>
/// <para><b>Why the duplicate-identifier check is not a substitute.</b> It fires only when a card number or a
/// national identifier was entered. Registration accepts a person with neither — a newly arrived refugee
/// frequently has neither — and those are precisely the registrations most likely to be re-submitted over a
/// poor connection. Two beneficiary rows for one human is the worst duplicate this platform can hold:
/// coverage, encounters, prescriptions and claims all attach to the id, so the two halves of a person's care
/// diverge permanently and neither record is complete.</para>
/// </remarks>
[Collection("patient-db")]
public class RegistrationIsIdempotentTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task The_same_registration_retried_under_the_same_key_creates_one_person()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var key = Guid.NewGuid().ToString();
            var body = Registration();

            var first = await PostAsync(registrar, key, body);
            first.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await first.Content.ReadAsStringAsync());
            var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("beneficiaryId").GetGuid();

            var replay = await PostAsync(registrar, key, body);

            // 200, not 201: nothing was created this time, and the status says so.
            replay.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await replay.Content.ReadAsStringAsync());
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("beneficiaryId").GetGuid()
                .Should().Be(id);

            await using var db = PatientApiFactory.Ctx();
            (await db.Beneficiaries.CountAsync(b => b.TenantId == app.Tenant)).Should().Be(1);
            // And exactly one registration APPLICATION. A second would put the same person in the approval
            // worklist twice, where two supervisors can decide the same arrival differently.
            (await db.Registrations.CountAsync(r => r.TenantId == app.Tenant)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_person_with_no_card_and_no_identifier_is_protected_too()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var key = Guid.NewGuid().ToString();
            // The case the duplicate checks cannot see. Two rows here would be invisible to every existing
            // guard, and this arrival — no card, no papers — is the one most likely to be re-submitted.
            var body = Registration() with { IdentifierValue = NewIdentifier() };

            var first = await PostAsync(registrar, key, body);
            first.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await first.Content.ReadAsStringAsync());
            await PostAsync(registrar, key, body);
            await PostAsync(registrar, key, body);

            await using var db = PatientApiFactory.Ctx();
            (await db.Beneficiaries.CountAsync(b => b.TenantId == app.Tenant)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_key_reused_for_a_DIFFERENT_person_is_refused_rather_than_answered_with_the_first()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var key = Guid.NewGuid().ToString();

            var first = await PostAsync(registrar, key, Registration());
            first.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await first.Content.ReadAsStringAsync());

            // A stuck key on the client, or an operator moving to the next arrival without the form
            // resetting. Answering this with the first person's record would hand the desk a 200 and a name
            // that is not the person in front of them — worse than the duplicate the ledger prevents.
            var different = await PostAsync(registrar, key, Registration() with
            {
                GivenName = "Yusuf", FamilyName = "Ibrahim", CardNumber = NewCard(),
                IdentifierValue = NewIdentifier(),
            });

            different.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await different.Content.ReadAsStringAsync()).Should().Contain("idempotency-key-reuse");

            await using var db = PatientApiFactory.Ctx();
            (await db.Beneficiaries.CountAsync(b => b.TenantId == app.Tenant)).Should().Be(1,
                "the second person was not registered — the desk must retry with their own key");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_refused_registration_does_not_burn_the_key()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var key = Guid.NewGuid().ToString();

            // A validation failure writes no ledger row, because the operator is going to correct the form
            // and press the same button again — and a browser's retry sends the same key. Claiming the key
            // on a request that created nothing would answer that correction with a 200 and no person.
            var invalid = await PostAsync(registrar, key, Registration() with { GivenName = "" });
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var corrected = await PostAsync(registrar, key, Registration());
            corrected.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await corrected.Content.ReadAsStringAsync());
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static string NewCard() => "C-" + Guid.NewGuid().ToString("N")[..10];
    private static string NewIdentifier() => "U-" + Guid.NewGuid().ToString("N")[..10];

    private static RegisterRequest Registration() => new(
        CardNumber: NewCard(), GivenName: "Amal", MiddleName: null, FamilyName: "Hassan",
        BirthDate: new DateOnly(1990, 3, 14), BirthDateIsApproximate: false, Sex: "Female",
        NationalityCode: "SD", IdentifierType: nameof(IdentifierType.UNHCRNo), IdentifierValue: NewIdentifier(),
        Phone: "+201000000000", IndividualNo: null, CaseNo: null,
        Enrolment: new EnrolmentIntentDto(Guid.NewGuid(), Guid.NewGuid(), 10m, null), Notes: null);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string key, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/beneficiaries", UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };
        req.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(req);
    }
}
