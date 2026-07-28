using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Identity;

namespace Mersal.Identity.Infrastructure;

/// <summary>The frozen-contract facts the issuer needs to mint a token for a user: subject, the flat
/// lower-case role set, the resolved scope union, and the ABAC attributes. The Api layer turns these into a
/// ClaimsPrincipal with the right token destinations. See docs/security/token-contract.md §2.</summary>
public sealed record UserTokenFacts(
    string Subject,
    IReadOnlyList<string> Roles,
    IReadOnlySet<string> Scopes,
    string TenantId,
    Guid? ProviderId,
    string DisplayName);

/// <summary>Assembles <see cref="UserTokenFacts"/> from the store: roles via Identity, scopes via the
/// roles/scopes-as-data resolver. This is the seam the OpenIddict token/authorize handlers call.</summary>
public sealed class UserClaimsService(UserManager<ApplicationUser> users, RoleScopeResolver resolver)
{
    public async Task<UserTokenFacts> ForAsync(ApplicationUser user, CancellationToken ct = default)
    {
        var roles = (await users.GetRolesAsync(user))
            .Select(r => r.ToLowerInvariant()).Distinct().ToList();
        // 21.1b — grants are tenant-local, so the user's tenant decides which grant set applies. A tenant
        // that has not been provisioned its own copy falls back to the platform default (design 40 §2).
        var scopes = await resolver.ResolveScopesAsync(roles, user.TenantId, ct);
        return new UserTokenFacts(user.Id.ToString(), roles, scopes, user.TenantId, user.ProviderId, user.DisplayName);
    }
}
