using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Mersal.Auth.Authorization;

/// <summary>
/// Generates authorization policies on demand so services can write
/// <c>[Authorize(Policy = HbmpPolicies.Scope("orders:consume"))]</c> without registering
/// every scope up front. Policy name convention: "scope:{scope}". Also serves the "mfa" policy.
/// Falls back to the default provider for any other policy name.
/// </summary>
public sealed class ScopePolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;
    private readonly bool _requireMfaForScopes;

    public ScopePolicyProvider(IOptions<AuthorizationOptions> options, IOptions<HbmpAuthOptions> authOptions)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
        _requireMfaForScopes = authOptions.Value.ProtectedScopeRequiresMfa;
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(HbmpPolicies.ScopePrefix, StringComparison.Ordinal))
        {
            // "scope:a" — one scope. "scope:a|b" — either (HbmpPolicies.AnyScope), for an endpoint two roles
            // reach legitimately without being owed the same powers.
            var scopes = policyName[HbmpPolicies.ScopePrefix.Length..]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ScopeRequirement(scopes, _requireMfaForScopes))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        if (string.Equals(policyName, HbmpPolicies.Mfa, StringComparison.Ordinal))
        {
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new MfaRequirement())
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}

/// <summary>Policy-name helpers for services.</summary>
public static class HbmpPolicies
{
    public const string ScopePrefix = "scope:";
    public const string Mfa = "mfa";

    /// <summary>Policy name requiring the given OAuth2 scope (and MFA when configured).</summary>
    public static string Scope(string scope) => ScopePrefix + scope;

    /// <summary>Policy name satisfied by ANY of the given scopes. Use when one endpoint is legitimately reached
    /// by roles that must not share powers — e.g. reserving an appointment (reception AND the call centre)
    /// versus checking a patient in (reception only).</summary>
    public static string AnyScope(params string[] scopes) => ScopePrefix + string.Join("|", scopes);
}
