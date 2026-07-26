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
}
