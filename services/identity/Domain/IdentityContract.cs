namespace Mersal.Identity.Domain;

/// <summary>
/// The frozen identity vocabulary the store MUST seed, mirroring docs/security/token-contract.md §2 (the 17
/// role names) so the roles-as-data seed and its tests share one source of truth. The role→scope MATRIX
/// itself lives in the migration seed (data), not here — this only pins the role NAMES that must exist.
/// </summary>
public static class IdentityContract
{
    /// <summary>The frozen, lower-case role vocabulary (authoritative catalog: admin RoleCatalog + the realm).</summary>
    public static readonly IReadOnlyList<string> Roles =
    [
        "reception", "call_center", "beneficiary_mgmt", "finance", "network_team", "claims_officer",
        "case_manager", "doctor", "nurse", "lab_tech", "imaging_tech", "pharmacist",
        "medical_approval", "medical_director", "provider_admin", "org_admin", "super_admin",
    ];

    /// <summary>The frozen OAuth scope vocabulary (docs/security/token-contract.md §2). Kept here so the
    /// OpenIddict issuer can register it and a test can assert it equals the DB seed (`identity.scope`).</summary>
    public static readonly IReadOnlyList<string> Scopes =
    [
        "admin:read", "admin:write", "admin:break-glass",
        "orders:read", "orders:consume", "orders:write",
        "pharmacy:read", "pharmacy:dispense",
        "auth:read", "auth:review", "auth:decide", "auth:emergency", "auth:override", "auth:manual", "auth:ingest",
        "reception:search",
        "emr:read", "emr:write", "encounter:write",
        "rx:write", "patient:write", "eligibility:check",
        "appointment:read", "appointment:write",
        "document:write",
        "case:read", "case:write", "case:manage",
        "finance:read", "finance:write", "finance:approve", "finance:export", "finance:project",
        "provider:read", "provider:write", "provider:finance",
        "policy:write", "referral:write",
        "callcentre:read", "callcentre:act", "callcentre:interaction", "callcentre:verify",
        "claims:read", "claims:reconcile", "claims:export",
        "reporting:read", "reporting:project", "reporting:export",
        "notification:read", "notification:ingest",
        "audit:read",
    ];

    /// <summary>
    /// 18.B1 (audit R2 X5) — the ONLY scopes the machine-to-machine client may hold.
    ///
    /// <c>hbmp-services</c> was seeded with every scope in <see cref="Scopes"/>, so a single leaked client
    /// secret minted a token that could read and write every beneficiary's PHI across the platform. A
    /// background worker never needs a clinician's or an administrator's authority: it ingests events and
    /// rebuilds projections. Anything a human does travels on that human's own bearer token.
    ///
    /// Adding a scope here is a reviewable change with a stated reason — it widens the blast radius of the
    /// service secret.
    /// </summary>
    public static readonly IReadOnlyList<string> ServiceScopes =
    [
        "auth:ingest",           // approvals: order/rx routed to the approval queue
        "notification:ingest",   // notification: enqueue from domain events
        "reporting:project",     // reporting: rebuild KPI read models
        "finance:project",       // finance: rebuild settlement projections
    ];

    /// <summary>Scopes an INTERACTIVE user session (the SPA public client) may request — everything except
    /// the machine-only ingest/projection scopes. A browser never rebuilds a projection.</summary>
    public static readonly IReadOnlyList<string> InteractiveScopes =
        [.. Scopes.Where(s => !ServiceScopes.Contains(s))];

    /// <summary>The audience/resource the frozen contract pins — services validate <c>aud = hbmp-api</c>.</summary>
    public const string ApiResource = "hbmp-api";

    /// <summary>The SPA's public client id (PKCE), carried forward from the Keycloak realm.</summary>
    public const string WebClientId = "hbmp-web";

    /// <summary>The confidential service-to-service client id (client-credentials).</summary>
    public const string ServiceClientId = "hbmp-services";
}
