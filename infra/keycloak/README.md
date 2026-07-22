# Keycloak — identity & access (realm-as-code)

Phase 0.2. Keycloak is the OIDC/OAuth2 IdP (MFA via TOTP/WebAuthn). The realm is provisioned **as code** and imported on first boot by the Compose stack (`--import-realm`).

## Files
- **`realm-mersal.json`** — the `mersal` realm: roles, clients, client scopes, MFA policy, session/timeout, brute-force lockout, protocol mappers.
- **`scope-catalog.yaml`** — the authoritative role → OAuth2 scope mapping, enforced at Kong (coarse) and each service (`libs/auth`, fine). Keep the two files in sync.

## Clients
| Client | Type | Flow | Use |
|--------|------|------|-----|
| `hbmp-api` | bearer-only resource server | — | Services validate access tokens for audience `hbmp-api`. Adds `roles`, `tenant_id`, `provider_id` claims + audience. |
| `hbmp-web` | public SPA | Authorization Code + PKCE (S256) | React portals; requests role-appropriate scopes; lands users on their portal. |

## MFA (mandatory)
`otpPolicyType: totp` + a default `CONFIGURE_TOTP` required action force second-factor enrolment. Tokens then carry `amr`/`acr` that `libs/auth` (`MfaEvaluator`) reads; scope-protected endpoints reject non-MFA tokens. For production, bind a browser flow that **requires** OTP (not just offers enrolment) and enable WebAuthn.

## Defense in depth
Token validation happens at **both** the Kong gateway (coarse, `jwt`/OIDC) **and** each service (`AddHbmpAuthentication`, full issuer/audience/JWKS/expiry + MFA). A bug in one layer does not bypass the other.

## Local dev
The Compose Keycloak is `start-dev` (HTTP, `RequireHttpsMetadata=false` for services). Realm URL: `http://keycloak:8080/realms/mersal` (in-cluster) / `http://localhost:8080/realms/mersal` (host). Admin console: `http://localhost:8080` (`KEYCLOAK_ADMIN` / `KEYCLOAK_ADMIN_PASSWORD` from `.env`). **Not production-hardened** — see `infra/compose/README.md`.

## Re-import after edits
The realm imports only on first boot. To re-apply changes: `docker compose rm -sf keycloak && docker volume rm <pg keycloak db>` (or use the admin CLI `kc.sh import`). In Tier 2/3 this is applied via CI (phase 8b policy/config admin).
