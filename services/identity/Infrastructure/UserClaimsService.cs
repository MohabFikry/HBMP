using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Identity;

namespace Mersal.Identity.Infrastructure;

/// <summary>The frozen-contract facts the issuer needs to mint a token for a user: subject, the flat
/// lower-case role set, the resolved scope union, and the ABAC attributes. The Api layer turns these into a
/// ClaimsPrincipal with the right token destinations. See docs/security/token-contract.md §2.
///
/// 21.1c — <see cref="MembershipId"/> is the active membership (ADR-0021, contract §2b). It is null only for
/// principals that genuinely have none (client-credentials machines).</summary>
public sealed record UserTokenFacts(
    string Subject,
    IReadOnlyList<string> Roles,
    IReadOnlySet<string> Scopes,
    string TenantId,
    Guid? ProviderId,
    string DisplayName,
    Guid? MembershipId = null,
    /// <summary>21.4 — the tenant's ENABLED programme switches (design 40 §4/§5). A gate, never a grant: a
    /// feature listed here still requires the endpoint's scope, and one absent can only subtract. Empty on the
    /// membership-less legacy path, which is correct — that path resolves no tenant context to switch on.</summary>
    IReadOnlyList<string>? Features = null);

/// <summary>Assembles <see cref="UserTokenFacts"/> from the store: roles + tenant + provider from the ACTIVE
/// MEMBERSHIP (design 40 §1, invariant 1 — authority lives on the membership, never the identity), scopes via
/// the tenant-local roles/scopes-as-data resolver. This is the seam the OpenIddict token/authorize handlers
/// call.</summary>
public sealed class UserClaimsService(
    UserManager<ApplicationUser> users, RoleScopeResolver resolver, MembershipService memberships,
    EffectiveSetService effective, TenantFeatureStore features)
{
    /// <summary>
    /// Facts for a user acting under <paramref name="membership"/>.
    ///
    /// Everything authority-bearing comes off the membership: the tenant it is in, the provider it is bound
    /// to, and the roles held through it. The identity contributes only its subject and display name. That is
    /// invariant 1 expressed in code — the same person under a different membership yields a different token.
    /// </summary>
    public async Task<UserTokenFacts> ForAsync(
        ApplicationUser user, TenantMembership membership, CancellationToken ct = default)
    {
        var roles = await memberships.RolesForAsync(membership.MembershipId, ct);

        // 21.2 MODE 1 — the token's scope claim is now the EFFECTIVE set, not the raw role grants: the
        // same algebra mode 2 runs, so per-membership allows and denies reach the token instead of being a
        // second, out-of-session opinion about the same question (design 40 §5, invariant 5).
        var scopes = (await effective.ComputeAsync(membership, "identity-service:token", ct)).Keys;

        // 21.4 — the THIRD gate rides along, resolved from the membership's TENANT rather than the identity:
        // the same person under a membership in another organisation gets that organisation's programme, which
        // is invariant 1 again. Read from the local projection of admin.tenant_feature, so issuing a token
        // makes no cross-service call.
        var enabled = await features.EnabledForAsync(membership.TenantId, ct);

        return new UserTokenFacts(
            user.Id.ToString(), roles, scopes,
            membership.TenantId, membership.ProviderId, user.DisplayName, membership.MembershipId, enabled);
    }

    /// <summary>
    /// Legacy path for an identity with no membership resolved.
    ///
    /// Kept ONLY so the expand phase is safe: 0010 backfills a membership for every existing user, but a user
    /// created between deploy and backfill would otherwise be unable to sign in at all. It reads the
    /// identity-level roles and tenant, exactly as before 21.1, and carries NO membership_id — so a token
    /// minted this way is visibly membership-less rather than pretending to have one. Remove with the
    /// contract migration that drops user_role.
    /// </summary>
    public async Task<UserTokenFacts> ForAsync(ApplicationUser user, CancellationToken ct = default)
    {
        var roles = (await users.GetRolesAsync(user))
            .Select(r => r.ToLowerInvariant()).Distinct().ToList();
        var scopes = await resolver.ResolveScopesAsync(roles, user.TenantId, ct);
        return new UserTokenFacts(user.Id.ToString(), roles, scopes, user.TenantId, user.ProviderId, user.DisplayName);
    }
}
