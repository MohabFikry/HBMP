# `libs/auth` — shared identity & access

Phase 0.2. The mandatory authentication library every HBMP service uses. Validates Keycloak-issued JWT access tokens and exposes the normalized principal + ABAC claims that `libs/authz` (0.4) and every service consume.

## What it does
- **Token validation** (`AddHbmpAuthentication`): issuer, audience, signature via **JWKS**, expiry, signed-tokens-required — at the service, in addition to the Kong gateway (defense in depth).
- **MFA enforcement**: scope-protected endpoints require an MFA-backed token (`acr`/`amr` inspected by `MfaEvaluator`). A non-MFA token is rejected for a protected scope.
- **Principal** (`HbmpPrincipal`): `sub`, roles (Keycloak `realm_access` + `resource_access`), scopes (`scope`), `tenant_id`, `provider_id`, `sid`, `src_ip`, `acr`/`amr`, `MfaSatisfied` — everything ABAC needs.
- **Scope authorization**: dynamic `scope:{name}` policies via `ScopePolicyProvider`; default-deny handlers audit every denial through `IAuthEventSink`.
- **Auth audit** (`IAuthEventSink`): login/failure, MFA-missing, token-rejected, authz-deny. A `NullAuthEventSink` stub until `libs/audit-client` (0.3) supplies the durable hash-chained sink.

## Usage
```csharp
// Program.cs
builder.Services.AddHbmpAuthentication(builder.Configuration);   // binds "Auth" section
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// An endpoint requiring a scope (and MFA, per config):
app.MapPost("/api/v1/investigation-orders/{id}/consume", Consume)
   .RequireAuthorization(HbmpPolicies.Scope("orders:consume"));

// Reading the caller:
app.MapGet("/whoami", (IHbmpPrincipalAccessor me) => me.Require().Subject)
   .RequireAuthorization();
```

## Config (`Auth` section)
| Key | Meaning | Dev value |
|-----|---------|-----------|
| `Auth:Authority` | Keycloak realm URL (JWKS discovered from it) | `http://keycloak:8080/realms/mersal` |
| `Auth:Audience` | expected `aud` | `hbmp-api` |
| `Auth:RequireHttpsMetadata` | HTTPS for OIDC metadata | `false` (Tier 1 only) |
| `Auth:ProtectedScopeRequiresMfa` | require MFA on scope-protected endpoints | `true` |

## Tests
`Tests/` (24 tests): claim extraction (Keycloak role/scope shapes, malformed JSON), `MfaEvaluator` matrix, scope/MFA authorization allow+deny+audit paths, and DI/policy-provider wiring. Run: `./dotnet.sh test libs/auth/Tests/Mersal.Auth.Tests.csproj`.
