using System.Security.Claims;
using System.Text.Json;

namespace Mersal.Auth;

/// <summary>
/// Helpers to read Keycloak's non-standard claim shapes off a validated principal:
/// roles live under <c>realm_access.roles</c> and <c>resource_access.{client}.roles</c>
/// (JSON), and scopes are a single space-delimited <c>scope</c> string.
/// </summary>
public static class KeycloakClaims
{
    /// <summary>Extract realm + client roles, normalized to lower-case, de-duplicated.</summary>
    public static IReadOnlySet<string> ExtractRoles(ClaimsPrincipal user)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);

        // Flat role claims (ClaimTypes.Role / "roles" / "role") mapped by some setups.
        foreach (var c in user.Claims)
        {
            if (c.Type is ClaimTypes.Role or "roles" or "role")
            {
                Add(roles, c.Value);
            }
        }

        // Keycloak nested realm_access.roles
        AddFromJsonArray(roles, user.FindFirstValue("realm_access"), "roles");

        // Keycloak nested resource_access.{client}.roles — merge every client's roles.
        var resourceAccess = user.FindFirstValue("resource_access");
        if (!string.IsNullOrWhiteSpace(resourceAccess))
        {
            try
            {
                using var doc = JsonDocument.Parse(resourceAccess);
                foreach (var client in doc.RootElement.EnumerateObject())
                {
                    if (client.Value.TryGetProperty("roles", out var rolesEl)
                        && rolesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rolesEl.EnumerateArray())
                        {
                            Add(roles, r.GetString());
                        }
                    }
                }
            }
            catch (JsonException) { /* malformed → ignore, deny-by-default handles access */ }
        }

        return roles;
    }

    /// <summary>Extract OAuth2 scopes from the space-delimited "scope" claim (+ "scp" fallback).</summary>
    public static IReadOnlySet<string> ExtractScopes(ClaimsPrincipal user)
    {
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

    /// <summary>Extract a multi-valued claim (e.g. amr) whether emitted as repeated claims or a JSON array.</summary>
    public static IReadOnlyList<string> ExtractMulti(ClaimsPrincipal user, string claimType)
    {
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

    private static void AddFromJsonArray(HashSet<string> target, string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray()) Add(target, el.GetString());
            }
        }
        catch (JsonException) { /* ignore malformed */ }
    }

    private static void Add(HashSet<string> set, string? role)
    {
        if (!string.IsNullOrWhiteSpace(role)) set.Add(role.ToLowerInvariant());
    }
}
