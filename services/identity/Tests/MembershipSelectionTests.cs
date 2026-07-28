using System.Net;
using FluentAssertions;
using Mersal.Identity.Domain;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.1c — the membership a session acts under, and the <c>membership_id</c> claim that records it
/// (design 40 §1 invariant 1, ADR-0021, token-contract §2b).
///
/// The property under test is that the MEMBERSHIP, not the identity, is the principal: the same person,
/// same password, same client, same request — a different selection — must produce a token with a different
/// tenant, different roles and different scopes. Everything else here guards the ways that property can be
/// quietly lost: a selection that is trusted instead of re-validated, a revocation that only takes effect at
/// the next login, or a chooser that cannot make progress.
///
/// Env-gated on IDENTITY_TEST_DB against a migrated database. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class MembershipSelectionTests
{
    private const string Password = "Passw0rd!Mersal";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    // Scopes spanning BOTH roles used below, so the request itself never decides the answer: the token's
    // contents have to come from the membership. Asking only for what one role grants would let a broken
    // resolver pass by accident.
    private const string Scope = "openid offline_access finance:read emr:read";

    [SkippableFact]
    public async Task Two_memberships_of_one_identity_mint_two_different_principals()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"ms-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, uname, Password, ["finance"]);

        try
        {
            var inA = await TestFlow.MembershipIdOf(factory, id, TestFlow.TenantA);
            var inB = await TestFlow.SeedMembership(factory, id, TenantB, ["doctor"]);

            var client = factory.CreateClient();
            var underA = await TestFlow.Validate(client,
                await TestFlow.AuthCodeToken(factory, uname, Password, null, Scope, inA));
            var underB = await TestFlow.Validate(client,
                await TestFlow.AuthCodeToken(factory, uname, Password, null, Scope, inB));

            // Same human being on both sides — that is what makes the rest of this test meaningful.
            underA.Subject.Should().Be(id.ToString());
            underB.Subject.Should().Be(id.ToString());

            underA.MembershipId.Should().Be(inA.ToString());
            underB.MembershipId.Should().Be(inB.ToString());
            underA.MembershipId.Should().NotBe(underB.MembershipId);

            // The tenant claim follows the membership, not the identity's own tenant_id column.
            underA.TenantId.Should().Be(TestFlow.TenantA);
            underB.TenantId.Should().Be(TenantB);

            // Authority differs, and differs in BOTH directions — neither token is a superset of the other.
            underA.Roles.Should().Contain("finance").And.NotContain("doctor");
            underB.Roles.Should().Contain("doctor").And.NotContain("finance");
            underA.Scopes.Should().Contain("finance:read").And.NotContain("emr:read");
            underB.Scopes.Should().Contain("emr:read").And.NotContain("finance:read");
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task A_single_membership_auto_selects_and_still_stamps_the_claim()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"ms1-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, uname, Password, ["finance"]);

        try
        {
            var (client, authorize, _) = await TestFlow.LoginThenAuthorize(factory, uname, Password, Scope);
            // One membership is not a choice, so nobody should be asked to make one.
            authorize.Headers.Location?.ToString().Should().NotContain("select-membership");

            var principal = await TestFlow.Validate(factory.CreateClient(),
                await TestFlow.AuthCodeToken(factory, uname, Password, null, Scope));
            // Auto-selection must still RECORD what it selected; a token whose membership is implied cannot be
            // audited or re-resolved later.
            principal.MembershipId.Should().Be((await TestFlow.MembershipIdOf(factory, id, TestFlow.TenantA)).ToString());
            client.Dispose();
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task Several_memberships_send_the_browser_to_the_chooser_rather_than_picking_one()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"msc-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, uname, Password, ["finance"]);

        try
        {
            await TestFlow.SeedMembership(factory, id, TenantB, ["doctor"]);

            var (client, authorize, _) = await TestFlow.LoginThenAuthorize(factory, uname, Password, Scope);

            // The refusal to guess IS the requirement. Silently picking the first membership would attribute
            // this person's actions to an organization they never chose.
            authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
            authorize.Headers.Location!.ToString().Should().StartWith("/connect/select-membership");

            var page = await client.GetStringAsync(authorize.Headers.Location!.ToString());
            page.Should().Contain(TestFlow.TenantA).And.Contain(TenantB);
            client.Dispose();
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task An_identity_with_no_selectable_membership_is_refused_a_token()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"msn-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, uname, Password, ["finance"]);

        try
        {
            // Suspending the only membership leaves a perfectly valid IDENTITY with no principal to act as.
            await TestFlow.SetMembershipStatus(
                factory, await TestFlow.MembershipIdOf(factory, id, TestFlow.TenantA), MembershipStatus.Suspended);

            var (client, authorize, _) = await TestFlow.LoginThenAuthorize(factory, uname, Password, Scope);

            // The password was still correct — authentication succeeded and AUTHORIZATION is what fails. The
            // dangerous outcome would be falling back to the identity's own roles, i.e. the blended principal
            // this phase removes, so the assertion is specifically that no code comes back.
            var location = authorize.Headers.Location?.ToString() ?? "";
            location.Should().NotContain("code=");
            (location.Contains("error=") || !authorize.IsSuccessStatusCode)
                .Should().BeTrue("a membership-less identity must be refused, not quietly issued a token");
            client.Dispose();
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task A_membership_belonging_to_someone_else_cannot_be_selected()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var mine = $"msx-{Guid.NewGuid():N}";
        var theirs = $"msy-{Guid.NewGuid():N}";
        var (myId, _) = await TestFlow.SeedUser(factory, mine, Password, ["finance"]);
        var (theirId, _) = await TestFlow.SeedUser(factory, theirs, Password, ["doctor"]);

        try
        {
            // Two memberships so I actually reach the chooser, then post a THIRD id that is not mine at all.
            await TestFlow.SeedMembership(factory, myId, TenantB, ["finance"]);
            var notMine = await TestFlow.MembershipIdOf(factory, theirId, TestFlow.TenantA);

            var (client, authorize, authorizeUrl) = await TestFlow.LoginThenAuthorize(factory, mine, Password, Scope);
            var chooserUrl = authorize.Headers.Location!.ToString();

            var posted = await TestFlow.ChooseMembership(client, chooserUrl, notMine, authorizeUrl);

            // A membership id is not a secret and the form is user-controlled, so accepting it unvalidated
            // would be a one-request path into another organization's tenant. Re-rendering the chooser (200)
            // rather than redirecting onward is the observable proof it was rejected.
            posted.StatusCode.Should().Be(HttpStatusCode.OK);
            var back = await client.GetAsync(authorizeUrl);
            back.Headers.Location?.ToString().Should().StartWith("/connect/select-membership",
                "the rejected selection must not have been stamped onto the cookie");
            client.Dispose();
        }
        finally
        {
            await TestFlow.DeleteUser(factory, myId);
            await TestFlow.DeleteUser(factory, theirId);
        }
    }

    [SkippableFact]
    public async Task Ending_a_membership_stops_its_refresh_token_at_the_next_exchange()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"msr-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, uname, Password, ["finance"]);

        try
        {
            var (_, refresh) = await TestFlow.AuthCodeTokens(factory, uname, Password, null, Scope);
            refresh.Should().NotBeNull("offline_access was requested, so a refresh token must be issued");

            await TestFlow.SetMembershipStatus(
                factory, await TestFlow.MembershipIdOf(factory, id, TestFlow.TenantA), MembershipStatus.Ended);

            var client = factory.CreateClient();
            var (status, body) = await TestFlow.PostTokenRaw(client, new()
            {
                ["grant_type"] = "refresh_token", ["client_id"] = IdentityContract.WebClientId,
                ["refresh_token"] = refresh!,
            });

            // This is the re-resolution seam ADR-0021 §3 depends on. If the token endpoint trusted the stored
            // grant instead of re-resolving the membership, ending someone's membership would leave them able
            // to renew their session indefinitely — revocation that revokes nothing.
            status.Should().Be(HttpStatusCode.BadRequest);
            body.Should().Contain("invalid_grant");
            client.Dispose();
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task Revoking_a_role_removes_it_from_the_membership_not_just_the_identity()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"msm-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, uname, Password, ["finance", "doctor"]);

        try
        {
            var client = factory.CreateClient();
            var before = await TestFlow.Validate(client, await TestFlow.AuthCodeToken(factory, uname, Password, null, Scope));
            before.Roles.Should().Contain("finance").And.Contain("doctor");

            await TestFlow.MirrorRoles(factory, id, ["finance"]);

            // Tokens are minted from membership_role, so a mirror that only ever ADDED rows would leave the
            // revoked role in every future token — revocation that looks done in the admin UI and changes
            // nothing in practice. The absence of emr:read is the same claim checked at the scope level.
            var after = await TestFlow.Validate(client, await TestFlow.AuthCodeToken(factory, uname, Password, null, Scope));
            after.Roles.Should().Contain("finance").And.NotContain("doctor");
            after.Scopes.Should().NotContain("emr:read");
            client.Dispose();
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task A_stale_selection_with_one_membership_left_reaches_the_chooser_instead_of_looping()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"msl-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, uname, Password, ["finance"]);

        try
        {
            var inA = await TestFlow.MembershipIdOf(factory, id, TestFlow.TenantA);
            var inB = await TestFlow.SeedMembership(factory, id, TenantB, ["doctor"]);

            // Sign in and choose A, so the cookie now names A.
            var (client, authorize, authorizeUrl) = await TestFlow.LoginThenAuthorize(factory, uname, Password, Scope);
            var chooserUrl = authorize.Headers.Location!.ToString();
            (await TestFlow.ChooseMembership(client, chooserUrl, inA, authorizeUrl))
                .StatusCode.Should().Be(HttpStatusCode.Redirect);

            // A is suspended mid-session, leaving exactly ONE other selectable membership. The cookie still
            // names A, so authorize cannot resolve and bounces to the chooser. If the chooser answered "only
            // one option, go back", authorize would fail on the same stale cookie and bounce again — an
            // infinite redirect loop that locks the account out of a tenant it can legitimately use.
            await TestFlow.SetMembershipStatus(factory, inA, MembershipStatus.Suspended);

            var again = await client.GetAsync(authorizeUrl);
            again.Headers.Location!.ToString().Should().StartWith("/connect/select-membership");

            var page = await client.GetAsync(again.Headers.Location!.ToString());
            page.StatusCode.Should().Be(HttpStatusCode.OK, "the single remaining option must be RENDERED, not redirected past");
            (await page.Content.ReadAsStringAsync()).Should().Contain(TenantB);

            // And the session can actually move on, which is what makes it not a loop.
            (await TestFlow.ChooseMembership(client, again.Headers.Location!.ToString(), inB, authorizeUrl))
                .StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await client.GetAsync(authorizeUrl)).Headers.Location!.ToString().Should().Contain("code=");
            client.Dispose();
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }
}
