using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Claims.Api;
using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>
/// Phase 24 Gate 3 — the claims money path, exercised through the ENDPOINTS.
///
/// <para>Everything asserted here is enforced in the Api layer and nowhere else: the authorization gate, the
/// provider-isolation filter, the min-necessary projection that reaches the wire, the Idempotency-Key
/// requirement, and the programme gate. claims-service had a thorough service-level suite and 0.0% Api
/// coverage, so all of it was unproven — a rule deleted from an endpoint would have failed no test while
/// every domain test stayed green.</para>
///
/// <para>Each test drives the real host over HTTP against the real Postgres, and cleans up by tenant scope
/// like the rest of the claims DB suite. Serialized through the claims-db collection.</para>
/// </summary>
[Collection("claims-db")]
public class ClaimsEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ClaimIntakeRequest Intake(string tenant, Guid fulfillmentRef, Guid providerId, decimal billed = 200m) => new(
        EventId: Guid.NewGuid(), EventType: "OrderLinesConsumed", TenantId: tenant,
        FulfillmentRef: fulfillmentRef, FulfillmentType: FulfillmentType.OrderFulfillment,
        BeneficiaryId: Guid.NewGuid(), ProviderId: providerId, ProviderLocationId: null, AuthorizationId: null,
        CodeSystem: ClaimCodeSystem.CPT, Code: "80053", Description: "Metabolic panel",
        Quantity: 1, BilledAmount: billed, ServiceDate: new DateOnly(2026, 7, 1), CurrencyCode: "EGP",
        OccurredAt: DateTimeOffset.UtcNow);

    // ---- intake -------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Intake_creates_a_claim_and_a_second_delivery_of_the_same_reference_is_refused()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var fref = Guid.NewGuid();
            var created = await officer.PostAsJsonAsync("/api/v1/claims/intake", Intake(app.Tenant, fref, Guid.NewGuid()), Web);
            created.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("outcome").GetString().Should().Be("Created");

            // A DIFFERENT event id for the SAME fulfillment reference is not a replay — it is a second claim
            // for one delivered service, which is the double-payment this endpoint exists to refuse.
            var again = await officer.PostAsJsonAsync("/api/v1/claims/intake", Intake(app.Tenant, fref, Guid.NewGuid()), Web);
            again.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await again.Content.ReadAsStringAsync()).Should().Contain("duplicate-claim");

            await using var db = ClaimsApiFactory.Ctx();
            (await db.ClaimLines.CountAsync(l => l.FulfillmentRef == fref)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Redelivering_the_identical_intake_event_is_a_replay_not_a_second_claim()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var ev = Intake(app.Tenant, Guid.NewGuid(), Guid.NewGuid());
            (await officer.PostAsJsonAsync("/api/v1/claims/intake", ev, Web)).StatusCode.Should().Be(HttpStatusCode.OK);

            var replay = await officer.PostAsJsonAsync("/api/v1/claims/intake", ev, Web);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            (await replay.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("outcome").GetString().Should().Be("Replayed");

            await using var db = ClaimsApiFactory.Ctx();
            (await db.ClaimLines.CountAsync(l => l.FulfillmentRef == ev.FulfillmentRef)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- min-necessary, on the serialized payload ---------------------------------------------------------

    /// <summary>
    /// INV-MIN-NECESSARY at the wire. FieldProjectorTests proves the projector; this proves what the claims
    /// endpoint actually SERIALIZES, which is the thing a caller receives. Asserted against the raw JSON
    /// text rather than a deserialized DTO, because a DTO can only be missing a field the test also knows
    /// to look for.
    /// </summary>
    [SkippableFact]
    public async Task A_claim_read_carries_codes_and_amounts_and_no_clinical_field()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var claimId = await SeedClaimAsync(app, officer, Guid.NewGuid());

            var read = await officer.GetAsync(new Uri($"/api/v1/claims/{claimId}", UriKind.Relative));
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await read.Content.ReadAsStringAsync();

            json.Should().Contain("\"code\":\"80053\"", "the service code is what a claim IS about");
            json.Should().Contain("billedAmount");
            foreach (var forbidden in new[] { "diagnosis", "icd", "note", "result", "observation", "report", "clinical" })
                json.Should().NotContainEquivalentOf(forbidden,
                    "claims ≠ diagnosis (11-permission-matrix §3.2): the clinical fields are ABSENT from this " +
                    "payload, not merely null");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- provider isolation -------------------------------------------------------------------------------

    /// <summary>INV-PROVIDER-ISOLATION, at the endpoint. The filter that forces a provider caller onto its own
    /// provider id lives in ClaimsEndpoints and has no equivalent in the service layer.</summary>
    [SkippableFact]
    public async Task A_provider_user_cannot_read_another_providers_claim_and_never_lists_one()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var mine = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            var myClaim = await SeedClaimAsync(app, officer, mine);
            var theirClaim = await SeedClaimAsync(app, officer, theirs);

            using var provider = app.ProviderScopedClient(mine);
            (await provider.GetAsync(new Uri($"/api/v1/claims/{myClaim}", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await provider.GetAsync(new Uri($"/api/v1/claims/{theirClaim}", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // ...and the list is filtered too, including when the caller ASKS for the other provider: the
            // request parameter must not be able to widen a provider caller's own scope.
            var listed = await provider.GetAsync(new Uri($"/api/v1/claims?providerId={theirs}", UriKind.Relative));
            listed.StatusCode.Should().Be(HttpStatusCode.OK);
            var rows = await listed.Content.ReadFromJsonAsync<List<JsonElement>>(Web);
            rows.Should().NotBeNull();
            rows!.Select(r => r.GetProperty("providerId").GetGuid()).Should().OnlyContain(p => p == mine);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// A FINDING, pinned rather than fixed. Three places say a provider user reads its own claims — the
    /// ClaimsEndpoints summary ("Provider users are isolated to their own claims"), the comment on the
    /// <c>claims:read</c> rule itself ("Provider users may read only their own claims"), and the isolation
    /// code in the handler — and the rule's role set is claims_officer / claims_reviewer / manager /
    /// finance, with no provider role in it. So a provider_admin, holding claims:read and its own provider
    /// id, is refused before that isolation code is ever reached: it can SUBMIT a claim and APPEAL a
    /// decision but cannot look at either.
    ///
    /// <para>Left as-is deliberately. The fix — adding a provider role to a read rule — WIDENS access to
    /// claims data, and which provider role, under which ABAC condition, is a product decision, not a
    /// tidy-up. This test records the behaviour that actually ships so the contradiction is visible and
    /// cannot drift further; if the rule is widened, this test fails and names why.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_provider_admin_is_currently_denied_claims_read()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var providerId = Guid.NewGuid();
            var claimId = await SeedClaimAsync(app, officer, providerId);

            using var providerAdmin = app.ProviderAdminClient(providerId);
            (await providerAdmin.GetAsync(new Uri($"/api/v1/claims/{claimId}", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                    "the claims:read rule grants no provider role, whatever the surrounding comments say");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- the decision gate --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_decision_without_an_idempotency_key_is_refused()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var claimId = await SeedClaimAsync(app, officer, Guid.NewGuid());
            var lineId = await FirstLineAsync(claimId);

            using var reviewer = app.ReviewerClient();
            var r = await reviewer.PostAsJsonAsync(
                $"/api/v1/claims/{claimId}/lines/{lineId}/decisions",
                new { decision = "Approve" }, Web);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync()).Should().Contain("idempotency-key-required");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// Replaying a decision under the same Idempotency-Key returns the ORIGINAL decision and writes no
    /// second one. The money consequence of getting this wrong is a line approved twice.
    /// </summary>
    [SkippableFact]
    public async Task Replaying_a_decision_key_returns_the_first_decision_and_writes_no_second()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var claimId = await SeedClaimAsync(app, officer, Guid.NewGuid());
            var lineId = await FirstLineAsync(claimId);

            using var reviewer = app.ReviewerClient();
            var key = Guid.NewGuid().ToString();
            var first = await DecideAsync(reviewer, claimId, lineId, key, new { decision = "Approve" });
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            var decisionId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("decisionId").GetGuid();

            var replay = await DecideAsync(reviewer, claimId, lineId, key, new { decision = "Approve" });
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
            replayed.GetProperty("outcome").GetString().Should().Be("Replayed");
            replayed.GetProperty("decisionId").GetGuid().Should().Be(decisionId);

            await using var db = ClaimsApiFactory.Ctx();
            (await db.ClaimDecisions.CountAsync(d => d.ClaimLineId == lineId)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// INV-DECIDED-LINE-CLOSED and the SoD rule that names it: the same reviewer coming back to a line they
    /// already closed is refused as SOD_SAME_DECIDER, not as a bare conflict — the message has to tell them
    /// what they did wrong. A DIFFERENT Idempotency-Key, so this is not the replay path above.
    /// </summary>
    [SkippableFact]
    public async Task A_decided_line_cannot_be_decided_again_by_the_same_reviewer()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var claimId = await SeedClaimAsync(app, officer, Guid.NewGuid());
            var lineId = await FirstLineAsync(claimId);

            using var reviewer = app.ReviewerClient();
            (await DecideAsync(reviewer, claimId, lineId, Guid.NewGuid().ToString(), new { decision = "Approve" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var second = await DecideAsync(reviewer, claimId, lineId, Guid.NewGuid().ToString(),
                new { decision = "Deny", reasonCodes = new[] { ReasonCodes.NotCoveredCategory }, rationale = "changed my mind" });
            second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await second.Content.ReadAsStringAsync()).Should().Contain("SOD_SAME_DECIDER");

            await using var db = ClaimsApiFactory.Ctx();
            var line = await db.ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == lineId);
            line.Status.Should().Be(ClaimLineStatus.Approved, "the refused second decision changed nothing");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>X3-ALLOWED-CAPPED-AT-TARIFF, at the endpoint: an approval above the contract tariff is
    /// rejected as a validation failure, not silently clamped on the way in.</summary>
    [SkippableFact]
    public async Task An_allowed_amount_above_the_contract_tariff_is_refused()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory { Tariff = 150m };
        try
        {
            using var officer = app.OfficerClient();
            var claimId = await SeedClaimAsync(app, officer, Guid.NewGuid(), billed: 200m);
            var lineId = await FirstLineAsync(claimId);

            using var reviewer = app.ReviewerClient();
            var r = await DecideAsync(reviewer, claimId, lineId, Guid.NewGuid().ToString(),
                new { decision = "PartiallyApprove", allowedAmount = 175m, reasonCodes = new[] { ReasonCodes.NotCoveredCategory } });
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadAsStringAsync()).Should().Contain("allowed-exceeds-cap");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- the gates in front of all of it ------------------------------------------------------------------

    [SkippableFact]
    public async Task An_unauthenticated_caller_gets_401_and_a_wrong_scope_gets_403()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync(new Uri("/api/v1/claims", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Finance may read and export. It holds no decide scope, and claims ≠ diagnosis is not the only
        // separation that matters — finance deciding a claim is the segregation this refuses.
        using var finance = app.FinanceClient();
        var decide = await DecideAsync(finance, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(),
            new { decision = "Approve" });
        decide.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The programme gate (design 40 §4), asked after authorization and before execution. A tenant whose
    /// claims programme is off is refused even holding every claims scope — and this is the third gate, so
    /// a test that only checks scopes would report it as working while it was removed.
    /// </summary>
    [SkippableFact]
    public async Task A_tenant_with_the_claims_programme_off_is_refused_despite_holding_every_scope()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        // Authorized in every other respect, and on OTHER programmes — just not this one. An EMPTY features
        // header would not test this: HttpClient drops a header with no value, and the handler would fall
        // through to its "every tenant is on its programmes" default and admit the call.
        using var offProgramme = app.As(ClaimsTestAuth.OfficerSub, "claims_officer",
            "claims:read claims:ingest claims:adjudicate claims:review claims:decide",
            features: ProgramFeatures.Emr + " " + ProgramFeatures.Orders);

        var r = await offProgramme.GetAsync(new Uri("/api/v1/claims", UriKind.Relative));
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("type").GetString().Should().Be(ProgramEnablement.NotEnabledType,
            "the remedy is 'ask Mersal to enable the programme', not 'ask your administrator for the permission'");
        problem.RootElement.GetProperty("feature").GetString().Should().Be(ProgramFeatures.Claims);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> DecideAsync(
        HttpClient client, Guid claimId, Guid lineId, string idempotencyKey, object body)
    {
        // Awaited INSIDE the using: returning the task would dispose the request — and its content — while
        // the send is still reading from it, which surfaces as ObjectDisposedException on StreamContent.
        using var req = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"/api/v1/claims/{claimId}/lines/{lineId}/decisions", UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: Web),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private static async Task<Guid> SeedClaimAsync(
        ClaimsApiFactory app, HttpClient officer, Guid providerId, decimal billed = 200m)
    {
        var r = await officer.PostAsJsonAsync("/api/v1/claims/intake", Intake(app.Tenant, Guid.NewGuid(), providerId, billed), Web);
        r.StatusCode.Should().Be(HttpStatusCode.OK, "the seed itself must succeed or the assertion below is vacuous");
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("claimId").GetGuid();
    }

    private static async Task<Guid> FirstLineAsync(Guid claimId)
    {
        await using var db = ClaimsApiFactory.Ctx();
        return await db.ClaimLines.AsNoTracking().Where(l => l.ClaimId == claimId)
            .Select(l => l.ClaimLineId).SingleAsync();
    }

}
