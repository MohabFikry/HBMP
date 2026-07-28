using Microsoft.AspNetCore.Identity;

namespace Mersal.Identity.Domain;

/// <summary>
/// An assignable application role (uuid key). The role NAME is the frozen, lower-case identifier the token's
/// <c>roles</c> claim carries (docs/security/token-contract.md) and the services' RBAC reads. A role grants
/// OAuth scopes through the <see cref="RoleScope"/> mapping — both roles and their scope grants are DATA
/// (17.1), so the 17.4 admin surface can manage them without a redeploy. <see cref="SensitivityTier"/>
/// mirrors <c>services/admin/Domain/RoleCatalog.cs</c> (T1–T4) and drives access-review cadence.
/// </summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }

    public ApplicationRole(string roleName) : base(roleName) { }

    /// <summary>Highest sensitivity tier the role habitually handles (T1–T4). T3/T4 recertify quarterly.</summary>
    public string SensitivityTier { get; set; } = "T1";

    /// <summary>Human-readable purpose of the role (bilingual copy lives in the SPA; this is an operator note).</summary>
    public string? Description { get; set; }

    /// <summary>
    /// 21.2 — the ordinal trust tier (design 40 §2). LOWER = MORE PRIVILEGED, seeded as 4 − sensitivity
    /// tier so the T4 platform-critical personas land at 0. Null for a role the seed did not cover.
    ///
    /// It answers ONLY tier-shaped questions — is this an administrative persona (MFA-required tiers per
    /// 17, peer-review-required grants per 8b). CAPABILITY QUESTIONS USE KEYS. Asking "level &lt;= 1" instead
    /// of asking for the key is how a case manager quietly acquires a doctor's reach, so the two are never
    /// substituted for one another (docs/CONVENTIONS.md).
    /// </summary>
    public int? Level { get; set; }
}
