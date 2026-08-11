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

    /// <summary>
    /// 28.13 — the person's JOB TITLE. "Senior Pharmacist", "Head of Reception", "Regional Coordinator".
    ///
    /// <para><b>It is not a role, and nothing authorizes on it.</b> The two are easy to conflate because both
    /// are short strings next to a person's name, so the distinction is worth stating plainly: a ROLE decides
    /// what the platform will let this account do and is drawn from a frozen vocabulary; a POSITION is what
    /// the organisation calls the job and is free text somebody types. An account can be a "Senior
    /// Pharmacist" holding the `reception` role, and the platform must keep answering to the role.</para>
    ///
    /// <para>Deliberately NOT a token claim. docs/security/token-contract.md is frozen and every claim in it
    /// is one that `libs/auth` or the SPA reads to make a DECISION; this is a caption. Putting it in the
    /// token would ship a display string to nineteen services that validate it, and make correcting a typo in
    /// somebody's job title wait for their access token to expire. The SPA reads it from
    /// <c>GET /identity/me/profile</c> instead.</para>
    ///
    /// <para>On the USER rather than the membership: the requirement is that it reads the same whichever
    /// portal the person is working in, and a membership-scoped title would by construction differ between
    /// two of them.</para>
    /// </summary>
    public string? Position { get; set; }

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
