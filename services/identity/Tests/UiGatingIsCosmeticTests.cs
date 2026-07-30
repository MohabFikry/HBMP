using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Identity.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.6 — UI GATING IS COSMETIC ONLY (design 40 §6, invariant 7).
///
/// The admin SPA hides affordances a caller cannot use. That is a usability choice and NOTHING ELSE: the
/// button is not the control, the API is. This suite is the standing proof — it hand-crafts the exact
/// requests a hidden button would have made and asserts the SERVER refuses them.
///
/// It asserts the API rather than the DOM on purpose. A test that checked "the button is not rendered"
/// would keep passing after someone made the endpoint permissive, which is the failure that actually
/// matters: the person who reaches these endpoints without the UI is not using the UI.
///
/// Env-gated on IDENTITY_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class UiGatingIsCosmeticTests(IdentityHostFixture host) : IClassFixture<IdentityHostFixture>
{
    private const string Password = "Passw0rd!Mersal";

    /// <summary>A caller with a perfectly valid token and NO administrative scope — the SPA would show them
    /// none of these affordances.</summary>
    private static async Task<HttpClient> NonAdminClient(IdentityAppFactory factory, string uname)
    {
        var token = await TestFlow.AuthCodeToken(factory, uname, Password, null, "openid reception:search");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [SkippableFact]
    public async Task A_hand_crafted_request_to_every_hidden_admin_action_is_refused()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var uname = $"cos-{Guid.NewGuid():N}";
        var victim = $"cosv-{Guid.NewGuid():N}";
        var (userId, _) = await TestFlow.SeedUser(factory, uname, Password, ["reception"]);
        var (victimId, _) = await TestFlow.SeedUser(factory, victim, Password, ["finance"]);

        try
        {
            var client = await NonAdminClient(factory, uname);
            var membershipId = await TestFlow.MembershipIdOf(factory, victimId, TestFlow.TenantA);

            // Each of these is exactly what a hidden button would have sent.
            var attempts = new (string Label, Func<Task<HttpResponseMessage>> Send)[]
            {
                ("grant an override", () => client.PostAsJsonAsync(
                    $"/identity/admin/memberships/{membershipId}/overrides",
                    new { scopeKey = "emr:read", effect = "Allow", reason = "I pressed the button" })),

                ("revoke an override", () => client.DeleteAsync(
                    $"/identity/admin/memberships/{membershipId}/overrides/emr:read")),

                ("preview someone's effective access", () => client.GetAsync(
                    $"/identity/admin/memberships/{membershipId}/effective")),

                ("read the tenant's access review", () => client.GetAsync(
                    $"/identity/admin/access-review/{TestFlow.TenantA}")),

                ("list another user's sessions", () => client.GetAsync(
                    $"/identity/admin/users/{victimId}/sessions")),

                ("sign another user out everywhere", () => client.DeleteAsync(
                    $"/identity/admin/users/{victimId}/sessions")),

                ("read another user's login history", () => client.GetAsync(
                    $"/identity/admin/users/{victimId}/login-history")),

                // 21.6 — the roster and detail behind the membership admin screens. Added with the screens
                // themselves: an endpoint that ships without an entry here is one whose only gate is a
                // button, which is no gate at all.
                ("list the tenant's memberships", () => client.GetAsync(
                    $"/identity/admin/memberships?tenant={TestFlow.TenantA}")),

                ("open one membership", () => client.GetAsync(
                    $"/identity/admin/memberships/{membershipId}")),

                // The per-session administrative revoke 21.6 added, so an administrator can kill one stolen
                // device without signing a clinician out of every device mid-shift.
                ("revoke one of another user's sessions", () => client.DeleteAsync(
                    $"/identity/admin/users/{victimId}/sessions/{Guid.NewGuid()}")),

                ("create a user", () => client.PostAsJsonAsync("/identity/admin/users", new
                {
                    username = "smuggled", displayName = "S", password = Password,
                    tenantId = TestFlow.TenantA, roles = new[] { "super_admin" },
                })),
            };

            foreach (var (label, send) in attempts)
            {
                var resp = await send();
                ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                    "a caller without the admin scope must be refused when they {0} — hiding the button is " +
                    "not enforcement, and the person who reaches this endpoint is not using the UI", label);
                resp.StatusCode.Should().BeOneOf(
                    HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
            }
        }
        finally
        {
            await TestFlow.DeleteUser(factory, userId);
            await TestFlow.DeleteUser(factory, victimId);
        }
    }

    [SkippableFact]
    public async Task A_user_cannot_reach_another_users_self_service_surfaces()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var mine = $"cosm-{Guid.NewGuid():N}";
        var theirs = $"cost-{Guid.NewGuid():N}";
        var (myId, _) = await TestFlow.SeedUser(factory, mine, Password, ["reception"]);
        var (theirId, _) = await TestFlow.SeedUser(factory, theirs, Password, ["reception"]);

        try
        {
            // Open a session belonging to someone else, then try to end it with MY token.
            Guid foreignSession;
            using (var scope = factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<SessionService>();
                foreignSession = (await svc.OpenAsync(theirId, null, "their-laptop", null)).SessionId;
            }

            var client = await NonAdminClient(factory, mine);
            var resp = await client.DeleteAsync($"/identity/me/sessions/{foreignSession}");

            // A session id is not a secret. Accepting it unvalidated would be a denial-of-service against a
            // colleague, and a way to force a re-login that a phishing page could then harvest.
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            using var check = factory.Services.CreateScope();
            var sessions = check.ServiceProvider.GetRequiredService<SessionService>();
            (await sessions.LiveAsync(theirId)).Should().ContainSingle(
                s => s.SessionId == foreignSession, "their session must still be live");
        }
        finally
        {
            await TestFlow.DeleteUser(factory, myId);
            await TestFlow.DeleteUser(factory, theirId);
        }
    }
}
