using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Patient.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Patient.Tests;

/// <summary>
/// Resolving a beneficiary from the identifiers a counter can read (phase 26.6, doc 43 §7).
/// </summary>
/// <remarks>
/// <para>
/// Three things were broken and are fixed together. <c>card_number</c> existed and was unique but no search
/// filter reached it and <c>IdentifierType</c> had no member for it. <c>GET /beneficiaries/resolve</c> —
/// which pharmacy-service has CALLED since phase 6 — did not exist, so the request 404'd, the client
/// swallowed it, and those search arms silently returned nothing.
/// </para>
/// <para>
/// The rule that matters is the second identifier. A card number is printed on something that gets shared,
/// photographed and reused; it is a lookup key and never proof of identity. Doc 43 D5 says to reuse the
/// phase-15 ≥2-identifier rule — that rule was deliberately deleted with the challenge screen it belonged
/// to, so it is implemented here instead, against the identifiers this endpoint actually receives.
/// </para>
/// </remarks>
[Collection("patient-db")]
public class CardNumberResolveTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A unique suffix per test. member_no is globally unique, so fixed fixture values collide with rows a
    /// previous run left behind — which fails as a duplicate-key error rather than as the assertion under
    /// test, and is a fault in the harness rather than in the code.
    /// </summary>
    private static string Unique() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static async Task<Guid> SeedAsync(PatientApiFactory app, string card, string memberNo, string passport)
    {
        await using var db = PatientApiFactory.Ctx();
        var id = Guid.NewGuid();
        db.Beneficiaries.Add(new Beneficiary
        {
            BeneficiaryId = id, TenantId = app.Tenant,
            GivenName = "Amina", FamilyName = "Yusuf",
            CardNumber = card, MemberNo = memberNo,
            Status = BeneficiaryStatus.Active,
            Identifiers =
            [
                new BeneficiaryIdentifier
                {
                    IdentifierId = Guid.NewGuid(), BeneficiaryId = id, TenantId = app.Tenant,
                    IdentifierType = IdentifierType.Passport,
                    IdentifierValue = IdentifierValidation.Normalize(passport),
                },
            ],
        });
        await db.SaveChangesAsync();
        return id;
    }

    [SkippableFact]
    public async Task ONE_IDENTIFIER_IS_NOT_ENOUGH_even_a_valid_card_number()
    {
        // The rule doc 43 D5 asks for. A card alone must not open a person's record.
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            var u = Unique();
            await SeedAsync(app, $"A-{u}", $"MRS-M-2026-{u}", $"P{u}11");
            using var client = app.ReceptionClient();

            var response = await client.GetAsync(
                new Uri($"/api/v1/beneficiaries/resolve?cardNumber=A-{u}", UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
            problem.GetProperty("title").GetString().Should().Be("two-identifiers-required");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Two_identifiers_resolve_the_beneficiary()
    {
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            var u = Unique();
            var id = await SeedAsync(app, $"A-{u}", $"MRS-M-2026-{u}", $"P{u}11");
            using var client = app.ReceptionClient();

            var response = await client.GetAsync(new Uri(
                $"/api/v1/beneficiaries/resolve?cardNumber=A-{u}&memberNo=MRS-M-2026-{u}", UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
            body.GetProperty("beneficiaryId").GetGuid().Should().Be(id);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_card_number_resolves_however_it_was_typed()
    {
        // "#A-1234", "a 1234" and "A1234" are one card. Without normalising both sides the counter's most
        // natural spelling — the one with the '#' printed on the card — would simply not be found.
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            var u = Unique();
            var id = await SeedAsync(app, $"A{u}", $"MRS-M-2026-{u}", $"P{u}11");
            using var client = app.ReceptionClient();

            foreach (var typed in new[] { $"A{u}", $"#A{u}", $"a{u}".ToLowerInvariant(), $"A {u}" })
            {
                var response = await client.GetAsync(new Uri(
                    $"/api/v1/beneficiaries/resolve?cardNumber={Uri.EscapeDataString(typed)}&memberNo=MRS-M-2026-{u}",
                    UriKind.Relative));

                response.StatusCode.Should().Be(HttpStatusCode.OK, "'{0}' is the same card", typed);
                (await response.Content.ReadFromJsonAsync<JsonElement>(Web))
                    .GetProperty("beneficiaryId").GetGuid().Should().Be(id);
            }
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Identifiers_that_match_DIFFERENT_people_resolve_to_nobody()
    {
        // The filters are ANDed. Two identifiers that each match someone, but not the same someone, is not a
        // match — returning either would be a coin toss over whose record is opened.
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            var u1 = Unique();
            var u2 = Unique();
            await SeedAsync(app, $"A-{u1}", $"MRS-M-2026-{u1}", $"P{u1}11");
            await SeedAsync(app, $"B-{u2}", $"MRS-M-2026-{u2}", $"P{u2}22");
            using var client = app.ReceptionClient();

            var response = await client.GetAsync(new Uri(
                $"/api/v1/beneficiaries/resolve?cardNumber=A-{u1}&memberNo=MRS-M-2026-{u2}", UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_resolved_payload_carries_NO_CLINICAL_FIELD()
    {
        // Doc 43 §7: retrieval by card number returns the minimum-necessary view. Asserted over the
        // SERIALIZED payload rather than over a DTO's declared properties, because what leaks is what is
        // written on the wire.
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            var u = Unique();
            await SeedAsync(app, $"A-{u}", $"MRS-M-2026-{u}", $"P{u}11");
            using var client = app.ReceptionClient();

            var raw = await client.GetStringAsync(new Uri(
                $"/api/v1/beneficiaries/resolve?cardNumber=A-{u}&memberNo=MRS-M-2026-{u}", UriKind.Relative));

            foreach (var forbidden in new[]
                     { "diagnos", "icd", "soap", "note", "prescription", "encounter", "allerg", "observation" })
            {
                raw.ToLowerInvariant().Should().NotContain(forbidden,
                    "a dispensing-context lookup discloses identity, never clinical content ('{0}')", forbidden);
            }
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_response_echoes_identifier_TYPES_and_never_their_values()
    {
        // The phase-15 privacy rule, kept: an audit trail records WHICH KINDS of identifier were used, not
        // the numbers themselves, so the log does not become a second copy of the identity record.
        Skip.If(PatientApiFactory.Db is null, "PATIENT_TEST_DB not set — DB integration test skipped.");
        await using var app = new PatientApiFactory();
        try
        {
            var u = Unique();
            await SeedAsync(app, $"A-{u}", $"MRS-M-2026-{u}", $"P{u}11");
            using var client = app.ReceptionClient();

            var body = await client.GetFromJsonAsync<JsonElement>(new Uri(
                $"/api/v1/beneficiaries/resolve?cardNumber=A-{u}&memberNo=MRS-M-2026-{u}", UriKind.Relative), Web);

            var matched = body.GetProperty("matchedOn").EnumerateArray().Select(x => x.GetString()).ToList();
            matched.Should().BeEquivalentTo(["CardNumber", "MemberNo"]);
            matched.Should().NotContain($"A-{u}");
        }
        finally { await app.CleanupAsync(); }
    }

    [Fact]
    public void CardNumber_is_a_first_class_identifier_type()
    {
        // It had no enum member at all, which is why no search filter could reach the column.
        Enum.IsDefined(IdentifierType.CardNumber).Should().BeTrue();
        IdentifierValidation.IsValid(IdentifierType.CardNumber, "#A-1234", out _).Should().BeTrue();
        IdentifierValidation.IsValid(IdentifierType.CardNumber, "", out _).Should().BeFalse();
    }
}
