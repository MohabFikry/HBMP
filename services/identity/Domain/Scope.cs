namespace Mersal.Identity.Domain;

/// <summary>
/// An OAuth2 scope the issuer can mint, held as DATA (17.1) rather than a hard-coded constant, so the
/// catalog and its role grants are administrable (17.4). The <see cref="Name"/> is the exact string the
/// services enforce per endpoint (e.g. <c>finance:read</c>) and that the token's space-delimited <c>scope</c>
/// claim carries. See docs/security/token-contract.md §2.
/// </summary>
public sealed class Scope
{
    public required string Name { get; set; }

    /// <summary>Coarse grouping for the admin UI (the domain prefix, e.g. <c>finance</c>, <c>orders</c>).</summary>
    public required string Domain { get; set; }

    public string? Description { get; set; }

    /// <summary>True for scopes only ever granted to a machine (client-credentials), never to a human role
    /// (e.g. <c>auth:ingest</c>, <c>notification:ingest</c>) — kept in the catalog for completeness.</summary>
    public bool ServiceOnly { get; set; }

    /// <summary>21.2 — superseded by <see cref="ReplacedBy"/>, but still resolving. Deprecation is a
    /// migration signal, not an enforcement one: a deprecated key keeps working (revoking access because
    /// someone renamed a key is an outage), it is excluded from newly seeded roles, and each consumer that
    /// still asks for it is logged ONCE so umbrella-splits are driven by evidence (design 40 §6).</summary>
    public bool Deprecated { get; set; }

    /// <summary>The key that supersedes this one, when <see cref="Deprecated"/>.</summary>
    public string? ReplacedBy { get; set; }

    /// <summary>
    /// A1 — this key governs PLATFORM ADMINISTRATION (tenant management, catalog management, identity
    /// administration), and is therefore the only kind of key the platform-admin flag may short-circuit.
    ///
    /// It is not a seniority marker and not a wildcard. The evaluator hard-excludes every key WITHOUT this
    /// flag from the short-circuit, so a platform administrator holding no membership can administer the
    /// platform and still cannot read a patient or a clinical field. Break-glass remains the only elevation
    /// into clinical data.
    /// </summary>
    public bool IsPlatformAdminKey { get; set; }
}

/// <summary>Whether a <see cref="MembershipOverride"/> adds a key or takes one away.</summary>
public enum OverrideEffect
{
    /// <summary>Grant a key the membership's roles do not carry.</summary>
    Allow,

    /// <summary>Withhold a key the membership's roles DO carry. Deny always wins over Allow.</summary>
    Deny,
}

/// <summary>
/// 21.2 — a per-membership exception to the role grants (design 40 §2): an explicit Allow or Deny of one
/// catalog key, attributed, justified, and optionally time-boxed.
///
/// This is the pressure-relief valve that stops bespoke roles from being invented. It is deliberately
/// narrow: one key, one membership, one reason. Overrides pass through the SAME
/// <c>SegregationOfDuties</c> engine as role grants — an override that would create a forbidden
/// combination is refused with the conflict reason, never quietly applied, because an exception path that
/// bypasses SoD is simply a way to hold both halves of a split duty.
///
/// Table: <c>identity.membership_override</c> (Migrations/0013_catalog_and_overrides.sql).
/// </summary>
public sealed class MembershipOverride
{
    public Guid OverrideId { get; set; }
    public Guid MembershipId { get; set; }

    /// <summary>The catalog key being allowed or denied.</summary>
    public required string ScopeKey { get; set; }

    public OverrideEffect Effect { get; set; }

    /// <summary>Why this exception exists. Required — an unexplained exception cannot be reviewed, and at
    /// access-review time is indistinguishable from a mistake.</summary>
    public required string Reason { get; set; }

    public string? GrantedBy { get; set; }

    /// <summary>When this override stops applying; null means indefinite. Evaluated at RESOLUTION time, so
    /// an expired override simply stops matching — there is no sweeper whose failure could leave access on.</summary>
    public DateTimeOffset? ValidUntil { get; set; }

    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether this override applies at <paramref name="asOf"/>. A soft-deleted or expired row is
    /// inert — fail closed for Allow, and (correctly) also inert for Deny, since an expired Deny is a
    /// restriction that was meant to end.</summary>
    public bool AppliesAt(DateTimeOffset asOf) => !IsDeleted && (ValidUntil is null || ValidUntil > asOf);
}

/// <summary>Append-only history twin of <see cref="MembershipOverride"/>.</summary>
public sealed class MembershipOverrideHistory
{
    public long HistoryId { get; set; }
    public Guid OverrideId { get; set; }
    public Guid MembershipId { get; set; }
    public string ScopeKey { get; set; } = string.Empty;
    public string Effect { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset? ValidUntil { get; set; }
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public string? ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Grants a <see cref="Scope"/> to an <see cref="ApplicationRole"/> by NAME (both stable identifiers). The
/// issuer resolves a user's roles → this mapping → the union of scope names for the <c>scope</c> claim.
/// </summary>
public sealed class RoleScope
{
    /// <summary>
    /// 21.1b — the tenant that OWNS this grant (design 40 §2). <see cref="PlatformDefault"/> ("") is the
    /// platform default set: the untenanted rows seeded by 0001, used for any tenant that has not been
    /// provisioned its own copy.
    ///
    /// Note what is and is not tenant-local: the ROLE CATALOG stays global (the token's <c>roles</c>
    /// vocabulary is frozen, and ASP.NET Identity's RoleStore requires globally unique role names), so
    /// tenant-locality lives here — two tenants may grant different scopes to the same role name.
    /// </summary>
    public string TenantId { get; set; } = PlatformDefault;

    public required string RoleName { get; set; }
    public required string ScopeName { get; set; }

    /// <summary>The platform default grant bucket — the rows every tenant falls back to until it is
    /// provisioned its own copy. Not a real tenant id.</summary>
    public const string PlatformDefault = "";
}
