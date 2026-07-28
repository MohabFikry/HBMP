using Mersal.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Infrastructure;

/// <summary>
/// Resolves a user's role set → the union of granted OAuth scopes, from the roles/scopes-as-data model.
/// This is the seam the OpenIddict issuer (17.2) calls to populate the token's space-delimited <c>scope</c>
/// claim: a finance user gets the <c>finance:*</c> scopes, never <c>orders:consume</c>. Role names are
/// compared lower-case (the frozen contract vocabulary). See docs/security/token-contract.md §2.
///
/// 21.1b — grants are TENANT-LOCAL (design 40 §2). The role catalog itself stays global (frozen <c>roles</c>
/// vocabulary; ASP.NET Identity requires globally unique role names), so the same role name may carry
/// different scopes in different tenants. Resolution takes the tenant's own grants when it has any, and
/// otherwise falls back to the platform default bucket — see <see cref="ResolveScopesAsync"/>.
/// </summary>
public sealed class RoleScopeResolver(IdentityStoreDbContext db)
{
    /// <summary>
    /// The distinct scope names granted to <paramref name="roleNames"/> within <paramref name="tenantId"/>
    /// (case-insensitive on role).
    ///
    /// PER-ROLE fallback, deliberately: a tenant that has provisioned its own copy but later has a role's
    /// grants cleared to empty must get EMPTY for that role, not the platform default — "this tenant's
    /// receptionists get nothing" is a legitimate configuration, and silently substituting the default would
    /// re-grant scopes an administrator explicitly removed. So the fallback asks "does this tenant define
    /// this role at all", not "does this tenant define anything".
    /// </summary>
    public async Task<IReadOnlySet<string>> ResolveScopesAsync(
        IEnumerable<string> roleNames, string? tenantId = null, CancellationToken ct = default)
    {
        var roles = roleNames.Select(r => r.Trim().ToLowerInvariant())
            .Where(r => r.Length > 0).Distinct().ToArray();
        if (roles.Length == 0) return new HashSet<string>(StringComparer.Ordinal);

        var tenant = tenantId ?? RoleScope.PlatformDefault;

        // One round trip: every grant for these roles in either the tenant's bucket or the default bucket.
        var rows = await db.RoleScopes.AsNoTracking()
            .Where(rs => roles.Contains(rs.RoleName)
                         && (rs.TenantId == tenant || rs.TenantId == RoleScope.PlatformDefault))
            .Select(rs => new { rs.TenantId, rs.RoleName, rs.ScopeName })
            .ToListAsync(ct);

        var tenantDefines = rows.Where(r => r.TenantId == tenant)
            .Select(r => r.RoleName).ToHashSet(StringComparer.Ordinal);

        var scopes = rows
            .Where(r => tenantDefines.Contains(r.RoleName)
                ? r.TenantId == tenant                       // tenant owns this role's grants
                : r.TenantId == RoleScope.PlatformDefault)   // not provisioned → platform default
            .Select(r => r.ScopeName);

        return new HashSet<string>(scopes, StringComparer.Ordinal);
    }
}
