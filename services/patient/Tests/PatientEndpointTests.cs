using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Patient.Api;
using Mersal.Patient.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Patient.Tests;

/// <summary>
/// Phase 24 Gate 3 — the beneficiary directory, over HTTP.
///
/// <para>18.B3 split reads from writes in the endpoint layer and nowhere else, and that split had no test.
/// It is the rule with the widest blast radius in this service: before it, the reception desk could not look
/// up the person in front of it, and anyone who could look someone up could rewrite their identity record.
/// A regression puts one of those two failures back.</para>
/// </summary>
[Collection("patient-db")]
public class PatientEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The 18.B3 separation, both directions. Reception READS the directory and is refused when it tries to
    /// write it; the registrar does both. Asserting only the refusal would pass on a service that refused
    /// everybody.
    /// </summary>
    [SkippableFact]
    public async Task Reception_may_look_someone_up_and_may_not_rewrite_them()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var (id, _) = await RegisterAsync(registrar);

            using var reception = app.ReceptionClient();
            (await reception.GetAsync(new Uri($"/api/v1/beneficiaries/{id}", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.OK,
                    "the desk cannot serve a person it is not allowed to find");

            var write = await PostAsync(reception, "/api/v1/beneficiaries", Guid.NewGuid().ToString(), Registration());
            write.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "reading the directory must not carry the power to rewrite an identity record");

            await using var db = PatientApiFactory.Ctx();
            (await db.Beneficiaries.CountAsync(b => b.TenantId == app.Tenant)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// Registering opens the review APPLICATION with the person, in the same transaction. Until 24.x nothing
    /// created these rows, so the US-003 worklist was empty unless someone hand-called POST /registrations,
    /// which no screen did — a beneficiary with no open application is a person nobody is going to review.
    /// </summary>
    [SkippableFact]
    public async Task Registering_a_beneficiary_opens_the_registration_application_with_them()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var (id, _) = await RegisterAsync(registrar);

            await using var db = PatientApiFactory.Ctx();
            var registration = await db.Registrations.AsNoTracking()
                .SingleAsync(r => r.BeneficiaryId == id);
            registration.Status.Should().Be(RegistrationStatus.Pending);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Registering_without_an_idempotency_key_is_refused()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var r = await PostAsync(registrar, "/api/v1/beneficiaries", null, Registration());
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync()).Should().Contain("idempotency-required");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// A duplicate IDENTIFIER and a duplicate CARD NUMBER get different problem types, because their remedies
    /// differ: the first means this is the same person and the operator should open them, the second usually
    /// means the card was mis-read or re-issued — and "open the existing record" would be wrong advice for a
    /// genuinely different person holding a recycled card.
    /// </summary>
    [SkippableFact]
    public async Task A_duplicate_identifier_and_a_duplicate_card_are_reported_as_different_problems()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var first = Registration();
            var (existingId, _) = await RegisterAsync(registrar, first);

            var sameIdentifier = await PostAsync(registrar, "/api/v1/beneficiaries", Guid.NewGuid().ToString(),
                first with { CardNumber = NewCard(), GivenName = "Different" });
            sameIdentifier.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var identifierBody = await sameIdentifier.Content.ReadAsStringAsync();
            identifierBody.Should().Contain("duplicate-identifier");
            identifierBody.Should().Contain(existingId.ToString(),
                "the response names the record to open, or the operator cannot act on it");

            var sameCard = await PostAsync(registrar, "/api/v1/beneficiaries", Guid.NewGuid().ToString(),
                first with { IdentifierValue = NewIdentifier() });
            sameCard.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await sameCard.Content.ReadAsStringAsync()).Should().Contain("duplicate-card-number",
                "a recycled card and the same person are different situations with different remedies");

            await using var db = PatientApiFactory.Ctx();
            (await db.Beneficiaries.CountAsync(b => b.TenantId == app.Tenant)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_registration_missing_mandatory_identity_fields_is_refused_per_field()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var r = await PostAsync(registrar, "/api/v1/beneficiaries", Guid.NewGuid().ToString(),
                Registration() with { GivenName = "", FamilyName = "" });
            ((int)r.StatusCode).Should().Be(400,
                "a validation problem names the fields, so the operator can fix them rather than guess");

            await using var db = PatientApiFactory.Ctx();
            (await db.Beneficiaries.CountAsync(b => b.TenantId == app.Tenant)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Lookup by identifier is how the desk finds someone who has no member number yet. A miss is a
    /// miss — an empty result, never somebody else's record.</summary>
    [SkippableFact]
    public async Task Lookup_by_identifier_finds_the_person_and_an_unknown_one_finds_nobody()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            using var registrar = app.RegistrarClient();
            var body = Registration();
            var (id, _) = await RegisterAsync(registrar, body);

            using var reception = app.ReceptionClient();
            var found = await reception.GetFromJsonAsync<JsonElement>(new Uri(
                $"/api/v1/beneficiaries?identifierType={body.IdentifierType}&identifierValue={body.IdentifierValue}",
                UriKind.Relative), Web);
            found.ToString().Should().Contain(id.ToString());

            var missed = await reception.GetAsync(new Uri(
                $"/api/v1/beneficiaries?identifierType={body.IdentifierType}&identifierValue=NOSUCHVALUE",
                UriKind.Relative));
            missed.StatusCode.Should().Be(HttpStatusCode.OK);
            (await missed.Content.ReadAsStringAsync()).Should().NotContain(id.ToString(),
                "a lookup miss returns nobody, never the nearest match");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_reaches_neither_the_directory_nor_a_record()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync(new Uri("/api/v1/beneficiaries?name=a", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(new Uri($"/api/v1/beneficiaries/{Guid.NewGuid()}", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static string NewCard() => "C" + Guid.NewGuid().ToString("N")[..11].ToUpperInvariant();
    private static string NewIdentifier() => "ID" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private static RegisterRequest Registration() => new(
        CardNumber: NewCard(), GivenName: "Amal", MiddleName: null, FamilyName: "Hassan",
        BirthDate: new DateOnly(1990, 3, 14), BirthDateIsApproximate: false, Sex: "Female",
        // ISO 3166-1 ALPHA-2, and the enrolment intent is mandatory: the coverage a registration elects is
        // what US-003 asks the approver to confirm, so a registration without one is a review with nothing
        // to review.
        NationalityCode: "SD", IdentifierType: nameof(IdentifierType.UNHCRNo), IdentifierValue: NewIdentifier(),
        Phone: "+201000000000", IndividualNo: null, CaseNo: null,
        Enrolment: new EnrolmentIntentDto(Guid.NewGuid(), Guid.NewGuid(), 10m, null), Notes: null);

    private static async Task<(Guid Id, RegisterRequest Body)> RegisterAsync(
        HttpClient client, RegisterRequest? body = null)
    {
        body ??= Registration();
        var r = await PostAsync(client, "/api/v1/beneficiaries", Guid.NewGuid().ToString(), body);
        r.StatusCode.Should().Be(HttpStatusCode.Created,
            "the seed must succeed or every assertion below is vacuous: {0}", await r.Content.ReadAsStringAsync());
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("beneficiaryId").GetGuid();
        return (id, body);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string? idempotencyKey, object body)
    {
        // Awaited inside the using: returning the task would dispose the content mid-send.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }
}

/// <summary>Serializes the patient endpoint tests against the shared patient store.</summary>
[Xunit.CollectionDefinition("patient-db", DisableParallelization = true)]
public sealed class PatientDbTestGroup;
