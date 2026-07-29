# Archive — superseded security artefacts

Historical reference only. **Nothing here is imported, run, or read by any service, script, or migration.**
Kept because it records what the platform's access model used to look like, which is occasionally needed when
reading an old ADR or audit finding.

## Keycloak realm-as-code (retired Phase 17, ADR-0015)

Keycloak was the OIDC/OAuth2 identity provider until Phase 17 replaced it with the in-app
**identity-service** (ASP.NET Core Identity + OpenIddict). Its Compose service was removed then; these two
files were the last of it, and lived on at `infra/keycloak/` — which read as live infrastructure sitting
beside the Compose and Helm directories that *are* live.

- **`keycloak-realm-mersal.json`** — the `mersal` realm: roles, clients, client scopes, MFA policy,
  session/timeout, brute-force lockout, protocol mappers.
- **`keycloak-scope-catalog.yaml`** — the role → OAuth2 scope mapping *as it stood in Phase 0.2*, 16 scopes.

> **Do not treat the catalogue as authoritative.** Its own README used to call it exactly that, while the live
> contract had grown past 79 scopes — so anyone who trusted it was reading a map four phases out of date. The
> authority now is the `identity.scope` / `identity.role_scope` tables (scopes-as-data, seeded by migration)
> and the frozen token contract in [`../token-contract.md`](../token-contract.md).

The retirement itself is recorded in [`../../adr/0015-in-app-identity-openiddict.md`](../../adr/0015-in-app-identity-openiddict.md).
