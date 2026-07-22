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
            var scope = policyName[HbmpPolicies.ScopePrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ScopeRequirement(scope, _requireMfaForScopes))
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
}
