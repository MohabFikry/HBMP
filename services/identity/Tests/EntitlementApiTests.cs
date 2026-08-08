using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Identity.Domain;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.11 — <c>GET /connect/entitlement</c>: what this caller WOULD be granted on a fresh authorisation.
///
/// <para>
/// It exists because the SPA cannot answer the question. A token's scopes are frozen at authorisation and the
/// refresh grant is constrained to the scopes on the stored grant, so an entitlement widened afterwards never
/// reaches a live session. The client cannot infer that by reading its own token: the gap between what it
/// asked for and what it holds is normally just least privilege working. The client-side guard that assumed
/// otherwise was false for every user in the system and signed people out on every page load.
/// </para>
/// <para>
/// So the ONE test that carries this file is <see cref="Reports_the_role_entitlement_not_the_token_scopes"/>.
/// Everything else here checks the endpoint is not a hole; that one checks it is worth having at all — an
/// endpoint that merely echoed the caller's own token back would pass a careless suite and tell the client
/// nothing it did not already know.
/// </para>
/// </summary>
[Collection("identity-db")]
public class EntitlementApiTests(IdentityHostFixture host) : IClassFixture<IdentityHostFixture>
{
    private const string Pass = "Passw0rd!Mersal";

    private sealed record Entitlement(string[] Scopes);

    [SkippableFact]
    public async Task Reports_the_role_entitlement_not_the_token_scopes()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — entitlement test skipped.");
        var factory = host.Factory;
        var user = $"ent-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, user, Pass, ["reception"]);
        try
        {
            // A DELIBERATELY narrow token: one platform scope, though reception is entitled to several. This is
            // the everyday shape — least privilege — and the endpoint must not mistake it for the answer.
            var token = await TestFlow.AuthCodeToken(factory, user, Pass, null, "openid offline_access appointment:read");
            var client = Authed(factory, token);

            var resp = await client.GetAsync("/connect/entitlement");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync<Entitlement>();

            body!.Scopes.Should().Contain("appointment:read");
            // The point of the endpoint, stated as an assertion: it reports MORE than the token carries,
            // because it is answering about the ROLE. An implementation that read the caller's scope claim and
            // handed it back would satisfy every other test in this file and be entirely useless.
            body.Scopes.Length.Should().BeGreaterThan(1,
                "reception is entitled to more than the single scope this token was minted with");
            body.Scopes.Should().Contain("patient:read",
                "an entitlement the narrow token does not carry is exactly what the client needs told");
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task Is_sorted_so_a_client_comparing_sets_is_not_comparing_iteration_order()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — entitlement test skipped.");
        var factory = host.Factory;
        var user = $"ent-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, user, Pass, ["reception"]);
        try
        {
            var token = await TestFlow.AuthCodeToken(factory, user, Pass, null, "openid offline_access appointment:read");
            var body = await (await Authed(factory, token).GetAsync("/connect/entitlement"))
                .Content.ReadFromJsonAsync<Entitlement>();

            body!.Scopes.Should().BeInAscendingOrder(StringComparer.Ordinal);
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task Refuses_an_anonymous_caller()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — entitlement test skipped.");
        var resp = await host.Factory.CreateClient().GetAsync("/connect/entitlement");

        // A user's authority is not public reference data. 401 rather than an empty list, so a client cannot
        // read "you are entitled to nothing" out of "you did not authenticate".
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task Answers_about_the_bearer_and_offers_no_way_to_ask_about_anyone_else()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — entitlement test skipped.");
        var factory = host.Factory;
        var mine = $"ent-{Guid.NewGuid():N}";
        var theirs = $"ent-{Guid.NewGuid():N}";
        var (myId, _) = await TestFlow.SeedUser(factory, mine, Pass, ["reception"]);
        var (theirId, _) = await TestFlow.SeedUser(factory, theirs, Pass, ["finance"]);
        try
        {
            var token = await TestFlow.AuthCodeToken(factory, mine, Pass, null, "openid offline_access appointment:read");
            var client = Authed(factory, token);

            // Minimum-necessary by construction: the subject comes from the token, so the obvious attempts to
            // redirect it are not refusals to write — there is simply no parameter to honour. Asserted anyway,
            // because "there is no parameter" is a property a future convenience overload could quietly remove.
            foreach (var query in new[] { $"?sub={theirId}", $"?userId={theirId}", $"?username={theirs}" })
            {
                var body = await (await client.GetAsync($"/connect/entitlement{query}"))
                    .Content.ReadFromJsonAsync<Entitlement>();
                body!.Scopes.Should().NotContain("finance:read",
                    $"'{query}' must not steer the answer towards another identity");
            }
        }
        finally
        {
            await TestFlow.DeleteUser(factory, myId);
            await TestFlow.DeleteUser(factory, theirId);
        }
    }

    [SkippableFact]
    public async Task Refuses_when_the_membership_the_session_was_issued_for_is_no_longer_active()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — entitlement test skipped.");
        var factory = host.Factory;
        var user = $"ent-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, user, Pass, ["reception"]);
        try
        {
            var token = await TestFlow.AuthCodeToken(factory, user, Pass, null, "openid offline_access appointment:read");
            var client = Authed(factory, token);

            await TestFlow.SetMembershipStatus(
                factory, await TestFlow.MembershipIdOf(factory, id, TestFlow.TenantA), MembershipStatus.Suspended);

            var resp = await client.GetAsync("/connect/entitlement");

            // Authority lives on the membership. Answering from the identity-level roles here would report an
            // entitlement no token minted for this session could ever carry, and a client acting on it would
            // re-authorise forever chasing scopes it cannot be granted.
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    private static HttpClient Authed(IdentityAppFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
