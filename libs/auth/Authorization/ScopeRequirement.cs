using Microsoft.AspNetCore.Authorization;

namespace Mersal.Auth.Authorization;

/// <summary>
/// Requires the caller's token to carry a specific OAuth2 scope, and (when configured)
/// to be MFA-backed. Registered dynamically per scope via <see cref="ScopePolicyProvider"/>
/// using the policy name "scope:{scope}".
/// </summary>
public sealed class ScopeRequirement : IAuthorizationRequirement
{
    public ScopeRequirement(string scope, bool requireMfa)
        : this(new[] { scope }, requireMfa) { }

    /// <summary>ANY-OF form: satisfied by whichever of <paramref name="scopes"/> the caller holds.
    ///
    /// Needed because one endpoint can be legitimately reachable by two roles that must NOT be given the same
    /// powers. Booking an appointment is the case: reception books AND checks patients in, while the call centre
    /// books and must never check anyone in. Requiring one scope for both operations forces a choice between
    /// giving the call centre check-in it should not have, or leaving it unable to book at all — which is
    /// exactly the state this replaced. Alternatives are OR-ed; the MFA gate still applies to all of them.</summary>
    public ScopeRequirement(IReadOnlyCollection<string> scopes, bool requireMfa)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0) throw new ArgumentException("At least one scope is required.", nameof(scopes));
        Scopes = scopes;
        RequireMfa = requireMfa;
    }

    /// <summary>The accepted scopes. Holding ANY one of them satisfies the requirement.</summary>
    public IReadOnlyCollection<string> Scopes { get; }

    /// <summary>The single scope, or a readable "a|b" for the any-of form (used in denial reasons/audit).</summary>
    public string Scope => string.Join("|", Scopes);

    public bool RequireMfa { get; }
}
