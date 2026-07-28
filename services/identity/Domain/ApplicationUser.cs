using Microsoft.AspNetCore.Identity;

namespace Mersal.Identity.Domain;

/// <summary>
/// The Mersal user, backing the in-app OIDC issuer (ADR-0015). Extends ASP.NET Identity's
/// <see cref="IdentityUser{TKey}"/> (uuid key) with the ABAC attributes the frozen token contract carries as
/// claims: <see cref="TenantId"/> (tenant isolation) and <see cref="ProviderId"/> (provider ownership, for
/// provider-scoped staff). <see cref="DisplayName"/> is the min-necessary display identity (no PHI).
/// Roles are assigned through Identity's user-role join; the resulting <c>roles</c> + resolved <c>scope</c>
/// claims come from the roles/scopes-as-data model (17.1). See docs/security/token-contract.md.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Tenant the user belongs to — emitted as the <c>tenant_id</c> claim (ABAC: tenant isolation).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Provider the user belongs to, for provider-scoped staff — emitted as <c>provider_id</c> (nullable).</summary>
    public Guid? ProviderId { get; set; }

    /// <summary>Min-necessary display name (never clinical). Surfaced as the token's <c>name</c> claim.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Soft-deactivation: a disabled account cannot authenticate (deprovisioning keeps the audit trail;
    /// no hard delete of identity records, per CLAUDE.md § Audit).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 21.1 (adaptation A1) — grants <b>platform administration only</b>: tenants, the scope catalog,
    /// identities, infrastructure config. It is <b>never a PHI wildcard</b>. The 21.2 evaluator short-circuits
    /// only catalog keys flagged as platform-administration keys and hard-excludes every clinical/benefit key;
    /// it does not bypass field projection, ABAC conditions, RLS, branch scope, or the sensitive gate
    /// (design 37 §6). Break-glass remains the only elevation into clinical data, and it is loud.
    /// Grantable only by another platform admin, and both sides of the grant are audited.
    /// </summary>
    public bool IsPlatformAdmin { get; set; }

    /// <summary>21.1 — this identity's memberships. THE principal is the membership, not this user
    /// (design 40 §1, invariant 1); an identity on its own authorizes nothing.</summary>
    public ICollection<TenantMembership> Memberships { get; } = [];
}
