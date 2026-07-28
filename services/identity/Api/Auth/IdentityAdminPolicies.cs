using System.Security.Claims;
using Mersal.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Phase 18.B3 (audit R2 S3) — framework-level authorization for the identity-service admin surface.
///
/// <c>/identity/admin/*</c> creates users, sets roles, rewrites the role→scope matrix and resets passwords.
/// It had NO <c>.RequireAuthorization</c> on its group: an unauthenticated request was routed, model-bound and
/// entered the handler, and was stopped only because every one of those six handlers remembers to call
/// <c>Guard</c> on its first line. That is a correct outcome resting on an unenforced convention — the seventh
/// endpoint someone adds is the one that forgets, and the failure is silent because nothing in the pipeline
/// knows the route was meant to be protected.
///
/// admin-service uses the shared <c>ScopePolicyProvider</c> for this. identity-service cannot: it authenticates
/// with OpenIddict's own validation scheme rather than the platform JWT handler, so the policies name that
/// scheme explicitly. <c>Guard</c> stays as layer two — it produces the specific 401/403 problem bodies the
/// admin SPA reads, and it is where the per-action scope (read vs write) is still decided.
/// </summary>
public static class IdentityAdminPolicies
{
    private const string Bearer = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;

    /// <summary>Authenticated on the issuer's own bearer scheme, holding an <c>admin:*</c> scope, with MFA
    /// satisfied. The narrowest gate that is still correct for every route in the group.</summary>
    public const string Admin = "identity-admin";

    /// <summary>Authenticated only. For the roles/scopes catalog, which is reference data rather than an
    /// administrative action but is not for anonymous eyes either.</summary>
    public const string Authenticated = "identity-authenticated";

    /// <summary>
    /// Whether an MFA step-up is required to reach the administrative surfaces.
    ///
    /// Honours the SAME switch every other service reads (<c>Auth:ProtectedScopeRequiresMfa</c>) and defaults
    /// to <b>true</b>, so deployed tiers are unchanged. The Tier-1 Compose starter sets it false because it
    /// seeds demo users WITHOUT enrolled TOTP: with the gate on, every admin screen answers 401/403 and the
    /// platform looks broken to anyone evaluating it locally. Never carry this into a Helm values file —
    /// a role grant or password reset without a step-up is precisely the finding this control exists for.
    /// </summary>
    private static bool RequiresMfa(IConfiguration config) =>
        config.GetValue("Auth:ProtectedScopeRequiresMfa", true);

    /// <summary>
    /// The resolved switch, for the per-handler <c>Guard</c> checks — the second layer that exists so the
    /// control does not depend on a route group being wired correctly. Set once at startup; a static rather
    /// than an injected option because those guards are static helpers on minimal-API handlers, and
    /// threading configuration through every one of them would be churn with no added safety.
    /// </summary>
    public static bool MfaRequired { get; private set; } = true;

    public static IServiceCollection AddIdentityAdminPolicies(this IServiceCollection services, IConfiguration config) =>
        services.AddAuthorization(o =>
        {
            var requireMfa = RequiresMfa(config);
            MfaRequired = requireMfa;
            o.AddPolicy(Authenticated, p => p
                .AddAuthenticationSchemes(Bearer)
                .RequireAuthenticatedUser());

            o.AddPolicy(Admin, p => p
                .AddAuthenticationSchemes(Bearer)
                .RequireAuthenticatedUser()
                // Either admin scope reaches the group; Guard then requires the right one for the action.
                // MFA is checked HERE as well as in Guard because it is the control that must not depend on a
                // handler remembering it — a role grant or a password reset without a step-up is the finding.
                .RequireAssertion(ctx =>
                    (HasScope(ctx.User, "admin:read") || HasScope(ctx.User, "admin:write"))
                    && (!requireMfa || MfaEvaluator.IsSatisfied(ctx.User.GetClaim(HbmpClaimTypes.Acr),
                                                                ctx.User.GetClaims(AccountPages.AmrClaim)))));
        });

    /// <summary>OAuth2 <c>scope</c> is a space-delimited string in one claim, and OpenIddict may also project
    /// it as repeated claims. Read both rather than assuming a shape.</summary>
    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.GetClaims(OpenIddictConstants.Claims.Scope)
            .SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
