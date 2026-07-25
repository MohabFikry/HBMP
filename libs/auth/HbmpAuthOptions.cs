namespace Mersal.Auth;

/// <summary>
/// Configuration for <c>AddHbmpAuthentication</c>. Bound from the "Auth" config section.
/// Example (appsettings / env):
///   Auth:Authority = http://keycloak:8080/realms/mersal
///   Auth:Audience  = hbmp-api
/// </summary>
public sealed class HbmpAuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC issuer / Keycloak realm URL. JWKS is discovered from its metadata.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Additional accepted token issuers beyond <see cref="Authority"/>. Needed when clients and
    /// services reach Keycloak on different hostnames (split-horizon dev: a browser mints a token via
    /// <c>http://localhost:8080/realms/…</c> while services fetch JWKS via <c>http://keycloak:8080/…</c>).
    /// When set, the token's <c>iss</c> may match any of these (plus the discovered Authority issuer).
    /// </summary>
    public string[] ValidIssuers { get; set; } = Array.Empty<string>();

    /// <summary>Expected token audience (aud) — the API's client id / resource.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Require HTTPS for OIDC metadata. Only false in local dev (Tier 1 Compose, http Keycloak).
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// When true (default), any scope-protected endpoint additionally requires an MFA-backed token.
    /// A non-MFA token is rejected for a protected scope (CLAUDE.md § Security).
    /// </summary>
    public bool ProtectedScopeRequiresMfa { get; set; } = true;

    /// <summary>Clock skew allowance for token expiry validation.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}
