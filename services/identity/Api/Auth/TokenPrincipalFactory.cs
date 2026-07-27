using System.Security.Claims;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Turns a user (or a client) into the ClaimsPrincipal the OpenIddict server signs, emitting the FROZEN token
/// contract with the correct token destinations. Access tokens carry sub / flat lower-case <c>roles</c> /
/// space-delimited <c>scope</c> / <c>tenant_id</c> / <c>provider_id</c> / <c>amr</c> / <c>name</c>, aud =
/// hbmp-api. See docs/security/token-contract.md §2 and libs/auth (HbmpPrincipal/MfaEvaluator).
/// </summary>
public sealed class TokenPrincipalFactory
{
    public const string RolesClaim = "roles";
    public const string TenantClaim = "tenant_id";
    public const string ProviderClaim = "provider_id";
    public const string AmrClaim = "amr";

    /// <summary>The OIDC/OAuth2 scopes that are not part of the platform vocabulary and are not role-derived.
    /// They govern the SHAPE of the response (an id token, a refresh token), not access to any resource, so
    /// they are granted on request rather than by entitlement.</summary>
    public static readonly IReadOnlySet<string> StandardScopes =
        new HashSet<string>(["openid", "profile", "email", "offline_access"], StringComparer.Ordinal);

    /// <summary>
    /// 18.B3 (audit R2 S5) — decide the scope set for a token request.
    ///
    /// The previous line was <c>SetScopes(granted.Length > 0 ? granted : facts.Scopes)</c>: when the requested
    /// scopes did not intersect the user's entitlement, it fell back to the user's ENTIRE entitlement. That
    /// inverts the purpose of the request. A client asking for a narrow, least-privilege token — or a
    /// compromised client asking for a scope it should not have — received strictly MORE authority than it
    /// asked for, and the down-scoping that a careful integration performs became the trigger for the
    /// broadest possible token. Now the intersection is granted unconditionally and an empty one is refused.
    /// </summary>
    /// <returns>The scopes to grant, or null when the request must be refused with <c>invalid_scope</c>.</returns>
    public static IReadOnlyCollection<string>? GrantableScopes(
        IReadOnlySet<string> userScopes, IEnumerable<string> requestedScopes)
    {
        var requested = requestedScopes.Distinct(StringComparer.Ordinal).ToArray();

        // Standard scopes pass through on request. Without this, `offline_access` — which the SPA asks for and
        // which OpenIddict requires on the principal to mint a refresh token — was silently dropped by the
        // intersection, because it is not a role-derived scope and never appears in userScopes. Every session
        // was therefore capped at one 5-minute access token with no way to renew (see W1).
        var standard = requested.Where(StandardScopes.Contains).ToArray();
        var platform = requested.Where(s => !StandardScopes.Contains(s)).ToArray();
        var grantedPlatform = platform.Where(userScopes.Contains).ToArray();

        // Asking for resource access and being entitled to none of it is a refusal, not a quieter token. A
        // token carrying only `openid` would let the client authenticate and then collect 403s from every
        // endpoint it tried, with nothing in the response explaining why.
        if (platform.Length > 0 && grantedPlatform.Length == 0) return null;
        if (standard.Length == 0 && grantedPlatform.Length == 0) return null;

        return [.. standard, .. grantedPlatform];
    }

    /// <summary>Build the principal for a signed-in user, granting the intersection of the user's resolved
    /// scopes and the requested scopes. <paramref name="amr"/> records the factors performed (e.g. pwd, otp).
    /// Returns null when no scope may be granted — the caller answers <c>invalid_scope</c>.</summary>
    public ClaimsPrincipal? ForUser(UserTokenFacts facts, IEnumerable<string> requestedScopes, IEnumerable<string> amr)
    {
        var granted = GrantableScopes(facts.Scopes, requestedScopes);
        if (granted is null) return null;

        var identity = new ClaimsIdentity(
            authenticationType: "mersal-issuer",
            nameType: Claims.Name, roleType: RolesClaim);

        identity.SetClaim(Claims.Subject, facts.Subject);
        identity.SetClaim(Claims.Name, facts.DisplayName);
        identity.SetClaim(TenantClaim, facts.TenantId);
        if (facts.ProviderId is { } pid) identity.SetClaim(ProviderClaim, pid.ToString());
        foreach (var role in facts.Roles) identity.AddClaim(new Claim(RolesClaim, role));
        foreach (var a in amr.Distinct(StringComparer.OrdinalIgnoreCase)) identity.AddClaim(new Claim(AmrClaim, a));

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(granted);
        principal.SetResources(IdentityContract.ApiResource);
        principal.SetDestinations(Destinations);
        return principal;
    }

    /// <summary>Per-claim token destinations. Everything the services' ABAC needs rides in the access token;
    /// sub + name also go to the id token.</summary>
    public static IEnumerable<string> Destinations(Claim claim) => claim.Type switch
    {
        Claims.Subject or Claims.Name => [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
        _ => [OpenIddictConstants.Destinations.AccessToken],
    };
}
