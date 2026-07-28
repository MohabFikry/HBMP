namespace Mersal.Auth;

/// <summary>
/// Canonical claim types the platform reads off a Keycloak-issued access token.
/// Keycloak emits some of these under non-standard names (realm_access.roles,
/// azp, etc.); <see cref="HbmpPrincipal"/> normalizes them.
/// See HBMP-Design/18-security-model.md (§ authentication, ABAC attributes).
/// </summary>
public static class HbmpClaimTypes
{
    /// <summary>Subject — the stable user id.</summary>
    public const string Subject = "sub";

    /// <summary>Authentication Context Class Reference (e.g. "urn:mace:...:mfa" or LoA).</summary>
    public const string Acr = "acr";

    /// <summary>Authentication Methods References (e.g. "pwd", "otp", "mfa", "hwk").</summary>
    public const string Amr = "amr";

    /// <summary>OAuth2 scopes, space-delimited in the standard "scope" claim.</summary>
    public const string Scope = "scope";

    /// <summary>Tenant the principal belongs to (ABAC: tenant isolation).</summary>
    public const string TenantId = "tenant_id";

    /// <summary>Provider the principal belongs to, for provider-scoped users (ABAC: provider-ownership).</summary>
    public const string ProviderId = "provider_id";

    /// <summary>Keycloak session id (sid) — correlates sessions for revoke/timeout.</summary>
    public const string SessionId = "sid";

    /// <summary>Source IP, surfaced for conditional access / audit (Keycloak can map this).</summary>
    public const string SourceIp = "src_ip";

    // ---- Phase 21 (ADR-0021) — ADDITIVE to the frozen contract. Every one of these is OPTIONAL:
    // a token minted before phase 21 carries none of them and MUST still yield the same principal.

    /// <summary>The active <c>tenant_membership</c> — THE security principal (design 40 §1). Authorization
    /// evaluates against this, never against <see cref="Subject"/>, because one identity may hold several
    /// memberships with different authority.</summary>
    public const string MembershipId = "membership_id";

    /// <summary>Ordinal trust tier of the active membership's role(s); lower = more privileged (design 40 §2).
    /// Answers TIER-shaped questions only (MFA-required tiers, peer-review-required grants). Capability
    /// questions use scope keys — never substitute one for the other.</summary>
    public const string Level = "level";

    /// <summary>Program-enablement feature switches for the membership's tenant (design 40 §4). Enablement is
    /// a gate, never a grant: a feature present here still requires the matching scope.</summary>
    public const string Features = "features";
}

/// <summary>
/// AMR / ACR values that indicate a multi-factor authentication was performed.
/// A token missing all of these is treated as single-factor and rejected for protected scopes.
/// </summary>
public static class MfaSignals
{
    /// <summary>AMR values that each independently indicate a second factor.</summary>
    public static readonly string[] Amr = ["mfa", "otp", "hwk", "totp", "webauthn", "sms"];

    /// <summary>ACR values Keycloak may emit for a step-up / MFA flow.</summary>
    public static readonly string[] Acr = ["mfa", "aal2", "aal3", "loa2", "loa3", "2fa"];
}
