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
        // 19.7 — the benefit-administration roles 19.1 deferred. `policy_admin` authors the product;
        // `beneficiary_mgmt_supervisor` is the supervisory increment over member administration (cancelling
        // another user's note, approving a retro-effective change). Both T2: neither reads clinical data.
        "policy_admin", "beneficiary_mgmt_supervisor",
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
        "rx:write", "patient:read", "patient:write", "eligibility:check",
        // appointment:reserve is booking WITHOUT the arrival decisions. The call centre reserves across
        // branches and must never check a patient in or mark a no-show; one write scope for both would have
        // forced granting it check-in it should not have.
        "appointment:read", "appointment:write", "appointment:reserve",
        "document:write",
        "case:read", "case:write", "case:manage",
        "finance:read", "finance:write", "finance:approve", "finance:export", "finance:project",
        // 19.1b — provider:admin is network ADMINISTRATION (create/retire a tier, move a provider between
        // tiers), split out of provider:write because a tier reassignment reprices every plan referencing that
        // tier while ordinary provider metadata edits do not.
        "provider:read", "provider:write", "provider:finance", "provider:admin",
        // 14.5 — the clinician PICKER, and nothing else. Booking filters on specialty then doctor, both of
        // which live in provider-service; granting the desk `provider:read` to reach them would hand it the
        // whole network directory (contracts, tariffs, tiers), and having emr fetch them under a service
        // account is forbidden platform-wide. Same split `patient:read` records: size the scope to the need.
        "practitioner:read",
        // 19.1 — the PAS split. policy:write already existed for MEMBER administration (enrol/terminate);
        // authoring the benefit product that members are enrolled onto is a separate, far heavier authority
        // (policy:admin), and policy:supervise is the supervisory increment (cancel another user's note,
        // approve a retro-effective change). policy:read is broad because the benefit configuration is the
        // vocabulary the whole platform adjudicates against — and it carries no PHI.
        "policy:read", "policy:write", "policy:admin", "policy:supervise", "referral:write",
        // 19.3 — notes are their own surface. note:read is deliberately wide (minimum-necessary bites at the
        // BODY, by visibility class, not at the surface); note:write is narrower because a note is a signed
        // statement. Cancelling another user's note additionally needs policy:supervise.
        "note:read", "note:write",
        "callcentre:read", "callcentre:act", "callcentre:interaction", "callcentre:verify",
        // 20 — the patient-profile authorities. profile:read is held by EVERY role the design-39 §4 matrix
        // names, because it is the COARSE gate: what each of them actually receives is decided per section by
        // ProfilePolicies, so a finance officer and a treating doctor carry the same scope and get profiles
        // with almost nothing in common. profile:export is narrower — copying a record out of the platform is
        // a different act from looking at it. callcentre:history:read is separate from callcentre:read because
        // it is held by roles that are not in the call centre at all and would have no business holding the
        // agent's workspace scope.
        "profile:read", "profile:export", "callcentre:history:read",
        // 18.B3/18.E1: the claims AUTHORITIES. Phase 10b's policy rules required these and the vocabulary
        // never listed them, so no token could carry one and the entire claims decision surface denied.
        // Each is a distinct authority by design — the officer who decides a line is not the person who
        // releases the settlement, and one scope covering both would erase that SoD split.
        "claims:read", "claims:reconcile", "claims:export",
        "claims:review", "claims:decide", "claims:adjudicate", "claims:adjust", "claims:batch",
        "claims:submit", "claims:reimburse:submit", "claims:appeal", "claims:settle", "claims:ingest",
        // 18.B3/18.E1: the eligibility card at the front desk (distinct from reception:search, which finds
        // the person), a prescriber reading back their own prescription, and the reporting FINANCIAL zone.
        "reception:read", "rx:read", "reporting:read-financial",
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
