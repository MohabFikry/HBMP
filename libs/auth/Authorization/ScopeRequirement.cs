using Microsoft.AspNetCore.Authorization;

namespace Mersal.Auth.Authorization;

/// <summary>
/// Requires the caller's token to carry a specific OAuth2 scope, and (when configured)
/// to be MFA-backed. Registered dynamically per scope via <see cref="ScopePolicyProvider"/>
/// using the policy name "scope:{scope}".
/// </summary>
public sealed class ScopeRequirement(string scope, bool requireMfa) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
    public bool RequireMfa { get; } = requireMfa;
}
