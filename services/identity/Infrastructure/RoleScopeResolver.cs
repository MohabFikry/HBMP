using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Infrastructure;

/// <summary>
/// Resolves a user's role set → the union of granted OAuth scopes, from the roles/scopes-as-data model.
/// This is the seam the OpenIddict issuer (17.2) calls to populate the token's space-delimited <c>scope</c>
/// claim: a finance user gets the <c>finance:*</c> scopes, never <c>orders:consume</c>. Role names are
/// compared lower-case (the frozen contract vocabulary). See docs/security/token-contract.md §2.
/// </summary>
public sealed class RoleScopeResolver(IdentityStoreDbContext db)
{
    /// <summary>The distinct scope names granted to <paramref name="roleNames"/> (case-insensitive on role).</summary>
    public async Task<IReadOnlySet<string>> ResolveScopesAsync(
        IEnumerable<string> roleNames, CancellationToken ct = default)
    {
        var roles = roleNames.Select(r => r.Trim().ToLowerInvariant())
            .Where(r => r.Length > 0).Distinct().ToArray();
        if (roles.Length == 0) return new HashSet<string>(StringComparer.Ordinal);

        var scopes = await db.RoleScopes.AsNoTracking()
            .Where(rs => roles.Contains(rs.RoleName))
            .Select(rs => rs.ScopeName)
            .Distinct()
            .ToListAsync(ct);

        return new HashSet<string>(scopes, StringComparer.Ordinal);
    }
}
