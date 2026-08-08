using System.Security.Claims;
using System.Text.Json;

namespace Mersal.Auth;

/// <summary>
/// Reads the platform's claim shapes off an already-validated principal: roles as repeated flat claims,
/// scopes as a single space-delimited <c>scope</c> string, and multi-valued claims (amr, features) as either
/// repeated claims or a JSON array.
///
/// <para><b>Why this no longer parses nested role objects.</b> Until Phase 17 (ADR-0015) the issuer was
/// Keycloak, which emits roles under <c>realm_access.roles</c> and <c>resource_access.{client}.roles</c>, and
/// this reader merged both. Keycloak is retired — identity-service emits flat lower-case <c>roles</c> claims
/// (see <c>TokenPrincipalFactory</c>) — so those two branches had no producer left. A ROLE SOURCE WITH NO
/// PRODUCER IS NOT HARMLESS DEAD CODE: it is a second, unowned way for authority to enter the authorization
/// path, and the only thing standing between it and a granted role was that nothing happened to emit that
/// shape. Removing it makes the set of things that can put a role in a token exactly one: our issuer.</para>
/// </summary>
public static class TokenClaims
{
    /// <summary>Extract role claims, normalized to lower-case, de-duplicated, and expanded across any open
    /// rename window (<see cref="LegacyRoleAliases"/>).</summary>
    public static IReadOnlySet<string> ExtractRoles(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var roles = new HashSet<string>(StringComparer.Ordinal);

        // The issuer emits `roles`; ClaimTypes.Role and `role` are accepted because the JWT handler's default
        // claim mapping can rename the first. All three are FLAT — a value is a role name, never a document
        // to be parsed for further roles.
        foreach (var c in user.Claims)
        {
            if (c.Type is ClaimTypes.Role or "roles" or "role")
            {
                Add(roles, c.Value);
            }
        }

        // 29.1 — THE dual-accept point for a role rename in flight (design 45 §1). Applied here, once, at the
        // token→principal boundary, so the ~40 downstream sites that compare role names keep working under
        // either spelling and none of them needs its own dual check to later remember to remove. It only ever
        // adds a second SPELLING of a role already on the token; it cannot add a role.
        return LegacyRoleAliases.WindowOpen ? LegacyRoleAliases.Expand(roles) : roles;
    }

    /// <summary>Extract OAuth2 scopes from the space-delimited "scope" claim (+ "scp" fallback).</summary>
    public static IReadOnlySet<string> ExtractScopes(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in user.Claims)
        {
            if (c.Type is HbmpClaimTypes.Scope or "scp")
            {
                foreach (var s in c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    scopes.Add(s);
                }
            }
        }
        return scopes;
    }

    /// <summary>Extract a multi-valued claim (e.g. amr, features) whether emitted as repeated claims or as a
    /// JSON array. Unlike roles these are not authority-bearing on their own — amr records what happened, and
    /// `features` can only ever subtract (see <c>ProgramEnablement</c>).</summary>
    public static IReadOnlyList<string> ExtractMulti(ClaimsPrincipal user, string claimType)
    {
        ArgumentNullException.ThrowIfNull(user);
        var values = new List<string>();
        foreach (var c in user.FindAll(claimType))
        {
            var v = c.Value.Trim();
            if (v.StartsWith('[') && v.EndsWith(']'))
            {
                try
                {
                    foreach (var el in JsonDocument.Parse(v).RootElement.EnumerateArray())
                    {
                        if (el.GetString() is { Length: > 0 } s) values.Add(s);
                    }
                    continue;
                }
                catch (JsonException) { /* fall through to raw */ }
            }
            if (v.Length > 0) values.Add(v);
        }
        return values;
    }

    private static void Add(HashSet<string> set, string? role)
    {
        if (!string.IsNullOrWhiteSpace(role)) set.Add(role.ToLowerInvariant());
    }
}
