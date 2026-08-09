using System.Security.Claims;

namespace Mersal.Auth;

/// <summary>
/// The authenticated principal, normalized from a validated access token, exposing
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

    /// <summary>The roles whose authority is bounded to ONE provider. A token in any of these must carry a
    /// <c>provider_id</c>; a token in none of them (the Network Team, platform admins) is legitimately
    /// tenant-wide.</summary>
    /// <remarks>
    /// Here rather than in provider-service because two layers need the same answer and were reading it from
    /// different places. provider-service rejects a provider-scoped token with no provider_id at the
    /// endpoint; the RLS binder, which has no view of provider-service's list, saw only an absent claim and
    /// bound the empty string — which the provider policies read as "tenant-wide". So the datastore layer,
    /// the one that is supposed to hold when the layers above it are wrong, was the layer that opened.
    /// One list, consulted by both.
    /// </remarks>
    private static readonly string[] ProviderScopedRoles =
        ["provider_admin", "lab_tech", "imaging_tech", "radiology_tech", "pharmacist"];

    /// <summary>True when this principal's authority is bounded to a single provider.</summary>
    public bool IsProviderScoped() => ProviderScopedRoles.Any(IsInRole);
    public string? SessionId { get; init; }
    public string? SourceIp { get; init; }

    /// <summary>Raw ACR value, if present.</summary>
    public string? Acr { get; init; }

    /// <summary>Raw AMR values, if present.</summary>
    public IReadOnlyList<string> Amr { get; init; } = [];

    /// <summary>True when the token evidences a second authentication factor (acr/amr).</summary>
    public bool MfaSatisfied { get; init; }

    // ---- Phase 21 (ADR-0021), additive. Null/empty on every pre-phase-21 token — see the byte-compat
    // fixtures in libs/auth/Tests/TokenContractByteCompatTests.cs.

    /// <summary>21 — the active membership (design 40 §1). THE security principal: one identity may hold
    /// several memberships with different authority, so authorization evaluates against this, not
    /// <see cref="Subject"/>. Null on a pre-phase-21 token and on client-credentials (no membership).</summary>
    public string? MembershipId { get; init; }

    /// <summary>21 — ordinal trust tier of the active membership (lower = more privileged, design 40 §2).
    /// TIER-shaped checks only; capability checks use <see cref="Scopes"/>. Null when the token omits it.</summary>
    public int? Level { get; init; }

    /// <summary>21 — the tenant's enabled program features (design 40 §4). A GATE, never a grant: presence
    /// here never substitutes for the scope the endpoint requires. Empty when the token omits it.</summary>
    public IReadOnlySet<string> Features { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether the membership's tenant has <paramref name="feature"/> enabled. False when the token
    /// carries no <c>features</c> claim at all — enablement is checked server-side against the store by the
    /// 21.4 middleware, and this is only the in-session fast path.</summary>
    public bool HasFeature(string feature) => Features.Contains(feature);

    public bool HasScope(string scope) => Scopes.Contains(scope);

    public bool IsInRole(string role) => Roles.Contains(role.ToLowerInvariant());

    /// <summary>
    /// Build a principal from a validated <see cref="ClaimsPrincipal"/>: flat <c>roles</c> claims, the
    /// space-delimited <c>scope</c> claim, and the multi-valued amr/features claims.
    /// </summary>
    public static HbmpPrincipal FromClaims(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var sub = user.FindFirstValue(HbmpClaimTypes.Subject)
                  ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? throw new InvalidOperationException("Access token has no subject (sub) claim.");

        var roles = TokenClaims.ExtractRoles(user);
        var scopes = TokenClaims.ExtractScopes(user);
        var amr = TokenClaims.ExtractMulti(user, HbmpClaimTypes.Amr);
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
            MembershipId = user.FindFirstValue(HbmpClaimTypes.MembershipId),
            Level = int.TryParse(user.FindFirstValue(HbmpClaimTypes.Level), out var lvl) ? lvl : null,
            Features = TokenClaims.ExtractMulti(user, HbmpClaimTypes.Features).ToHashSet(StringComparer.Ordinal),
        };
    }
}
