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
