using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 29.2 (design 45 §2, invariant 3) — <b>an E/M code creates a REFERRAL carrying the CPT code as its
/// requested service.</b>
///
/// <para>The routing decision existed and nothing acted on it: `CptRouting` returned
/// <c>OrderableVehicle.Referral</c> for every E/M code, `/orderable-services` published that verdict, and no
/// caller anywhere created a referral from it. The registry entry INV-EM-CODE-CREATES-REFERRAL claimed "an
/// E/M code CREATES a Referral" while its three named tests only asserted that a pure function returned the
/// right enum.</para>
///
/// <para><b>Why the code must travel with the referral.</b> A referral is not done when it is accepted — it
/// is done when a report comes back, and the loop is only closable against something specific. "Cardiology
/// opinion" with no requested service is the open-ended referral that never closes, which is the classic
/// outpatient safety failure the state machine exists to model.</para>
/// </summary>
[Collection("pharmacy-db")]
public class ReferralCarriesRequestedServiceTests(PrescribingApiFactory f) : IClassFixture<PrescribingApiFactory>
{
    private async Task<HttpResponseMessage> CreateAsync(object body)
    {
        var doctor = f.Prescriber("referral:write");
        doctor.DefaultRequestHeaders.Add("Idempotency-Key", $"ref-{Guid.NewGuid()}");
        return await doctor.PostAsJsonAsync("/api/v1/referrals", body);
    }

    [SkippableFact]
    public async Task A_referral_records_the_CPT_code_it_was_raised_for()
    {
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var r = await CreateAsync(new
        {
            beneficiaryId = f.Beneficiary,
            encounterId = Guid.NewGuid(),
            targetSpecialty = "Cardiology",
            reason = "Chest pain on exertion",
            requestedServiceCode = "99243",           // office consultation — an E/M code
            requestedServiceCodeSystem = "CPT",
        });

        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("referralId").GetGuid();

        await using var db = PrescribingApiFactory.Ctx();
        var referral = await db.Referrals.AsNoTracking().SingleAsync(x => x.ReferralId == id);

        referral.RequestedServiceCode.Should().Be("99243");
        referral.RequestedServiceCodeSystem.Should().Be("CPT");
    }

    [SkippableFact]
    public async Task The_requested_service_is_returned_to_the_caller()
    {
        // The ordering doctor's worklist shows what the referral is FOR. Storing it without returning it
        // would leave the loop closable only by someone with database access.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var r = await CreateAsync(new
        {
            beneficiaryId = f.Beneficiary,
            encounterId = Guid.NewGuid(),
            targetSpecialty = "Cardiology",
            requestedServiceCode = "99213",
            requestedServiceCodeSystem = "CPT",
        });

        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requestedServiceCode").GetString().Should().Be("99213");
    }

    [SkippableFact]
    public async Task A_referral_without_a_requested_service_is_still_accepted()
    {
        // ADDITIVE. Referrals predate this phase and are raised from paths that have no CPT code at all —
        // requiring one here would break every existing caller to serve a new one.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var r = await CreateAsync(new
        {
            beneficiaryId = f.Beneficiary,
            encounterId = Guid.NewGuid(),
            targetSpecialty = "Cardiology",
        });

        r.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [SkippableFact]
    public async Task A_NON_EM_code_is_refused_as_a_referral_service()
    {
        // The routing map runs in BOTH directions, and this is the half that keeps it honest. A knee
        // arthroscopy raised as a referral would bypass the consume/authorise/claim path a procedure order
        // goes through — the same class of mistake as routing E/M to a procedure, in the other direction.
        Skip.If(PrescribingApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        var r = await CreateAsync(new
        {
            beneficiaryId = f.Beneficiary,
            encounterId = Guid.NewGuid(),
            targetSpecialty = "Orthopaedics",
            requestedServiceCode = "29881",          // knee arthroscopy — Surgery, a Procedure order
            requestedServiceCodeSystem = "CPT",
        });

        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("not-a-referral-service");
    }
}
