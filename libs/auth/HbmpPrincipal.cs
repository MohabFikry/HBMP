using System.Security.Claims;

namespace Mersal.Auth;

/// <summary>
/// The authenticated principal, normalized from a Keycloak access token, exposing
/// every ABAC-relevant claim the services and <c>libs/authz</c> need. Immutable.
/// See HBMP-Design/18-security-model.md and CLAUDE.md § Security.
/// </summary>
public sealed record HbmpPrincipal
{
    public required string Subject { get; init; }

    /// <summary>Application roles (realm + client roles), lower-cased and de-duped.</summary>
    public required IReadOnlySet<string> Roles { get; init; }

    /// <summary>OAuth2 scopes granted to the token (from the space-delimited "scope" claim).</summary>
    public required IReadOnlySet<string> Scopes { get; init; }

    /// <summary>19.3 — the human-readable name to SIGN a note or document with, from <c>name</c> or
    /// <c>preferred_username</c>. Null when the token carries neither; callers that sign fall back to
    /// <see cref="Subject"/>, because a signature that says "u-1042" is recoverable and an unsigned record is
    /// not. Signatures are SNAPSHOTTED at write time, never joined — the point is that they survive the author
    /// being renamed or de-provisioned.</summary>
    public string? DisplayName { get; init; }

    public string? TenantId { get; init; }
    public string? ProviderId { get; init; }
    public string? SessionId { get; init; }
    public string? SourceIp { get; init; }

    /// <summary>Raw ACR value, if present.</summary>
    public string? Acr { get; init; }

    /// <summary>Raw AMR values, if present.</summary>
    public IReadOnlyList<string> Amr { get; init; } = [];

    /// <summary>True when the token evidences a second authentication factor (acr/amr).</summary>
    public bool MfaSatisfied { get; init; }

    public bool HasScope(string scope) => Scopes.Contains(scope);

    public bool IsInRole(string role) => Roles.Contains(role.ToLowerInvariant());

    /// <summary>
    /// Build a principal from a validated <see cref="ClaimsPrincipal"/>. Handles Keycloak's
    /// nested <c>realm_access.roles</c> / <c>resource_access.*.roles</c> role shapes and the
    /// space-delimited <c>scope</c> claim.
    /// </summary>
    public static HbmpPrincipal FromClaims(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var sub = user.FindFirstValue(HbmpClaimTypes.Subject)
                  ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? throw new InvalidOperationException("Access token has no subject (sub) claim.");

        var roles = KeycloakClaims.ExtractRoles(user);
        var scopes = KeycloakClaims.ExtractScopes(user);
        var amr = KeycloakClaims.ExtractMulti(user, HbmpClaimTypes.Amr);
        var acr = user.FindFirstValue(HbmpClaimTypes.Acr);

        return new HbmpPrincipal
        {
            Subject = sub,
            Roles = roles,
            Scopes = scopes,
            DisplayName = user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username"),
            TenantId = user.FindFirstValue(HbmpClaimTypes.TenantId),
            ProviderId = user.FindFirstValue(HbmpClaimTypes.ProviderId),
            SessionId = user.FindFirstValue(HbmpClaimTypes.SessionId),
            SourceIp = user.FindFirstValue(HbmpClaimTypes.SourceIp),
            Acr = acr,
            Amr = amr,
            MfaSatisfied = MfaEvaluator.IsSatisfied(acr, amr),
        };
    }
}
