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

    /// <summary>Build the principal for a signed-in user, granting the intersection of the user's resolved
    /// scopes and the requested scopes. <paramref name="amr"/> records the factors performed (e.g. pwd, otp).</summary>
    public ClaimsPrincipal ForUser(UserTokenFacts facts, IEnumerable<string> requestedScopes, IEnumerable<string> amr)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "mersal-issuer",
            nameType: Claims.Name, roleType: RolesClaim);

        identity.SetClaim(Claims.Subject, facts.Subject);
        identity.SetClaim(Claims.Name, facts.DisplayName);
        identity.SetClaim(TenantClaim, facts.TenantId);
        if (facts.ProviderId is { } pid) identity.SetClaim(ProviderClaim, pid.ToString());
        foreach (var role in facts.Roles) identity.AddClaim(new Claim(RolesClaim, role));
        foreach (var a in amr.Distinct(StringComparer.OrdinalIgnoreCase)) identity.AddClaim(new Claim(AmrClaim, a));

        var granted = facts.Scopes.Intersect(requestedScopes, StringComparer.Ordinal).ToArray();

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(granted.Length > 0 ? granted : facts.Scopes);
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
