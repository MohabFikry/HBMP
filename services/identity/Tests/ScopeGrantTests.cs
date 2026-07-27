using FluentAssertions;
using Mersal.Identity.Api.Auth;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 18.B3 (audit R2 S5) — what the issuer puts in the <c>scope</c> claim.
///
/// The old rule was <c>granted.Length > 0 ? granted : facts.Scopes</c>: no intersection meant the user's
/// ENTIRE entitlement. Down-scoping — the thing a careful client does — was the trigger for the broadest
/// possible token.
/// </summary>
public class ScopeGrantTests
{
    private static IReadOnlySet<string> User(params string[] scopes) =>
        new HashSet<string>(scopes, StringComparer.Ordinal);

    [Fact]
    public void The_grant_is_the_intersection_and_nothing_more()
    {
        var granted = TokenPrincipalFactory.GrantableScopes(
            User("emr:read", "emr:write", "patient:read", "orders:consume"),
            ["emr:read", "patient:read"]);

        granted.Should().BeEquivalentTo(["emr:read", "patient:read"],
            "a client that asks for two scopes must not receive four");
    }

    [Fact]
    public void No_overlap_is_refused_rather_than_widened()
    {
        // The finding, stated directly: this input used to return the user's whole entitlement.
        TokenPrincipalFactory.GrantableScopes(User("emr:read", "emr:write"), ["finance:export"])
            .Should().BeNull("the request must fail with invalid_scope, not succeed with more authority");
    }

    [Fact]
    public void A_scope_the_user_does_not_hold_is_dropped_from_a_partial_match()
    {
        TokenPrincipalFactory.GrantableScopes(User("emr:read"), ["emr:read", "admin:write"])
            .Should().BeEquivalentTo(["emr:read"], "the unheld scope is dropped; the held one still works");
    }

    [Fact]
    public void Standard_oidc_scopes_survive_the_intersection()
    {
        // offline_access is not role-derived, so it is not in the user's scope set — and the intersection used
        // to drop it. OpenIddict needs it ON THE PRINCIPAL to mint a refresh token, so every session was
        // capped at one 5-minute access token with no way to renew (this is half of W1).
        var granted = TokenPrincipalFactory.GrantableScopes(
            User("emr:read"), ["openid", "offline_access", "emr:read"]);

        granted.Should().Contain("offline_access").And.Contain("openid").And.Contain("emr:read");
    }

    [Fact]
    public void An_authentication_only_request_is_allowed()
    {
        // openid alone is a legitimate request: authenticate the user, grant no resource access.
        TokenPrincipalFactory.GrantableScopes(User("emr:read"), ["openid", "profile"])
            .Should().BeEquivalentTo(["openid", "profile"]);
    }

    [Fact]
    public void Asking_for_resource_access_and_being_entitled_to_none_is_refused_even_with_openid()
    {
        // Otherwise the client gets a token, calls the API, and collects 403s with nothing explaining why.
        // invalid_scope says it once, at the point the client can still do something about it.
        TokenPrincipalFactory.GrantableScopes(User("emr:read"), ["openid", "finance:export"])
            .Should().BeNull();
    }

    [Fact]
    public void An_empty_request_grants_nothing()
    {
        TokenPrincipalFactory.GrantableScopes(User("emr:read"), []).Should().BeNull();
    }

    [Fact]
    public void A_user_with_no_entitlement_cannot_obtain_resource_scopes()
    {
        TokenPrincipalFactory.GrantableScopes(User(), ["emr:read"]).Should().BeNull();
    }
}
