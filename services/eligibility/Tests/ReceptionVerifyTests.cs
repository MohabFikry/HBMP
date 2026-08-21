using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Eligibility.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Tests;

/// <summary>
/// 33.9 — <c>POST /reception/verify</c>, over HTTP.
/// </summary>
/// <remarks>
/// <para><b>What this replaced.</b> The eligibility screen called <c>/reception/search</c> with one free-text
/// box and then checked <c>hits[0]</c>. Typing "Ahmed" matched every Ahmed on the platform, the database's
/// ordering chose one, and the plan, remaining cap and visit verdict on screen belonged to a person nobody
/// had picked — with nothing on the card to say there had been others. A desk turning somebody away, or
/// admitting them, on another member's coverage.</para>
///
/// <para><b>Why over HTTP and not against the rule.</b> <see cref="IdentityCorroborationTests"/> covers the
/// matching rule as a function, and that is the right place for it. What can only be seen at this level is
/// what the RESPONSE carries — specifically that a refusal carries no identity. That is a property of the
/// wire, and a test below the endpoint would pass while the endpoint leaked.</para>
/// </remarks>
[Collection("eligibility-db")]
public class ReceptionVerifyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The front desk, holding the scope the reception group is gated on.
    /// </summary>
    /// <remarks>
    /// Not <c>CheckerClient()</c>, which carries <c>eligibility:check</c> only. Verifying WHO the member is
    /// and asking whether they are covered are separate grants and the endpoints sit behind separate gates —
    /// a caller that may run a check on a beneficiary id it already holds is not thereby allowed to resolve
    /// one from a card.
    /// </remarks>
    private static HttpClient Desk(EligibilityApiFactory app) =>
        app.As("11111111-1111-1111-1111-111111111111", "reception", "reception:search eligibility:check");

    private static StringContent Body(string? identifier, string? name) =>
        new(JsonSerializer.Serialize(new { identifier, name }, Web), System.Text.Encoding.UTF8, "application/json");

    [SkippableFact]
    public async Task An_identifier_and_a_matching_name_return_the_one_member_they_name()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        var id = Guid.NewGuid();
        try
        {
            await SeedAsync(app, id, "MRS-M-2026-VER01", "Amal", "Hassan", "29001019876543");

            using var desk = Desk(app);
            var r = await desk.PostAsync(new Uri("/api/v1/reception/verify", UriKind.Relative),
                Body("MRS-M-2026-VER01", "Hassan"));

            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());
            var body = await r.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("verified").GetBoolean().Should().BeTrue();
            body.GetProperty("card").GetProperty("identity").GetProperty("beneficiaryId").GetGuid().Should().Be(id);
            body.GetProperty("card").GetProperty("identity").GetProperty("displayName").GetString()
                .Should().Be("Amal Hassan");
        }
        finally { await CleanupAsync(app, id); }
    }

    /// <summary>
    /// The refusal that matters, and the thing it must not say.
    /// </summary>
    /// <remarks>
    /// A response of "no — that card belongs to Amal Hassan" would hand the name behind any card number to
    /// whoever is holding one, which is a worse disclosure than the defect this endpoint replaces. So the
    /// mismatch carries a reason code and nothing else: no name, no member number, no membership status.
    /// </remarks>
    [SkippableFact]
    public async Task A_wrong_name_is_refused_and_the_refusal_names_nobody()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        var id = Guid.NewGuid();
        try
        {
            await SeedAsync(app, id, "MRS-M-2026-VER02", "Amal", "Hassan", "29001019876544");

            using var desk = Desk(app);
            var r = await desk.PostAsync(new Uri("/api/v1/reception/verify", UriKind.Relative),
                Body("MRS-M-2026-VER02", "Khalil"));

            var raw = await r.Content.ReadAsStringAsync();
            var body = JsonSerializer.Deserialize<JsonElement>(raw);
            body.GetProperty("verified").GetBoolean().Should().BeFalse();
            body.GetProperty("reason").GetString().Should().Be("name-mismatch");
            body.TryGetProperty("card", out var card).Should().BeTrue();
            card.ValueKind.Should().Be(JsonValueKind.Null);

            // Asserted on the RAW body rather than on the parsed shape: the point is that these strings are
            // not anywhere in what crossed the wire, including somewhere a future field would put them.
            raw.Should().NotContain("Amal").And.NotContain("Hassan");
            raw.Should().NotContain("Active", "the refusal does not disclose the membership status either");
            raw.Should().NotContain(id.ToString());
        }
        finally { await CleanupAsync(app, id); }
    }

    [SkippableFact]
    public async Task An_unknown_identifier_is_told_apart_from_a_wrong_name()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        using var desk = Desk(app);

        var r = await desk.PostAsync(new Uri("/api/v1/reception/verify", UriKind.Relative),
            Body("MRS-M-2026-NOSUCHCARD", "Hassan"));

        // Two situations, two actions at the desk: re-read the digits, or ask them to say their name again.
        // Collapsing them would leave an operator unable to tell a typo from the wrong person. The cost —
        // that a card holder learns the card is registered — is the smaller disclosure, and the mismatch is
        // audited at High severity so a run of them across different numbers is findable.
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
            .Should().Be("not-found");
    }

    /// <summary>
    /// One letter is refused, which is what keeps the fix a fix.
    /// </summary>
    /// <remarks>
    /// A single character prefix-matches a large fraction of any name list. Accepting it would restore the
    /// old behaviour at the cost of one keystroke — type the card number, add "A", and you are back to
    /// opening whoever the database returns first.
    /// </remarks>
    [SkippableFact]
    public async Task A_single_letter_is_refused_rather_than_treated_as_a_name()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        var id = Guid.NewGuid();
        try
        {
            await SeedAsync(app, id, "MRS-M-2026-VER03", "Amal", "Hassan", "29001019876545");

            using var desk = Desk(app);
            var r = await desk.PostAsync(new Uri("/api/v1/reception/verify", UriKind.Relative),
                Body("MRS-M-2026-VER03", "A"));

            var body = await r.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("verified").GetBoolean().Should().BeFalse();
            body.GetProperty("reason").GetString().Should().Be("name-too-short");
        }
        finally { await CleanupAsync(app, id); }
    }

    /// <summary>
    /// A name on its own is not a way in — which is the whole difference between this and the search it
    /// replaced.
    /// </summary>
    [SkippableFact]
    public async Task A_name_with_no_identifier_is_refused_at_the_door()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        using var desk = Desk(app);

        var r = await desk.PostAsync(new Uri("/api/v1/reception/verify", UriKind.Relative), Body("", "Hassan"));

        // 400, not a refusal card: the desk has not asked a question this endpoint can answer. There is no
        // path from a name fragment to a member here at all.
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A partial card number finds nobody.
    /// </summary>
    /// <remarks>
    /// The lookup is equality on each column and never ILIKE. A prefix match would let a half-read number
    /// resolve the first member whose card begins that way — the same wrong-person failure arriving through
    /// the identifier instead of the name.
    /// </remarks>
    [SkippableFact]
    public async Task A_partial_identifier_resolves_nobody()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");
        await using var app = new EligibilityApiFactory();
        var id = Guid.NewGuid();
        try
        {
            await SeedAsync(app, id, "MRS-M-2026-VER04", "Amal", "Hassan", "29001019876546");

            using var desk = Desk(app);
            var r = await desk.PostAsync(new Uri("/api/v1/reception/verify", UriKind.Relative),
                Body("MRS-M-2026-VER", "Hassan"));

            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be("not-found");
        }
        finally { await CleanupAsync(app, id); }
    }

    private static async Task SeedAsync(
        EligibilityApiFactory app, Guid id, string memberNo, string given, string family, string nationalId)
    {
        await using var db = EligibilityApiFactory.Ctx();
        db.Members.Add(new MemberProjection
        {
            TenantId = app.Tenant, BeneficiaryId = id, MemberNo = memberNo,
            GivenName = given, FamilyName = family, NationalId = nationalId,
            Status = "Active", UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(EligibilityApiFactory app, Guid id)
    {
        if (EligibilityApiFactory.Db is null) return;
        await using var db = EligibilityApiFactory.Ctx();
        await db.Coverages.Where(c => c.BeneficiaryId == id).ExecuteDeleteAsync();
        await db.Members.Where(m => m.BeneficiaryId == id).ExecuteDeleteAsync();
    }
}
