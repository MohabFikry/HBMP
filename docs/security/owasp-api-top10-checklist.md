# OWASP API Security Top 10 (2023) — Mersal HBMP checklist (Phase 11.2)

Run against every public `/api/v1` endpoint through Kong. Goal: **0 unauthorized-access
defects** (NFR-031, NFR-040/041). "Evidence" links the automated test or scan that proves it.
Minimum-necessary (row + field) per `11-*` is the core control — most items below reduce to it.

| # | Risk | Control in Mersal | Evidence | Status |
|---|---|---|---|---|
| API1 | **BOLA** (broken object-level authz) | ABAC row filter (treating-relationship, provider-ownership, tenant, branch) + Postgres RLS on every read; a role requesting an object it may not see gets default-deny 403/404 | Per-service authz tests (e.g. doctor sees only assigned patients; provider isolation; branch-scope); cross-role BOLA matrix | Green (unit/integration) |
| API2 | **Broken authentication** | Keycloak OIDC, short-lived JWT, MFA (TOTP/WebAuthn), audience+issuer validated at Kong+service, brute-force lockout | `libs/auth` tests; Keycloak realm config | Green |
| API3 | **Broken object property level authz** (excessive data / mass assignment) | Field-level min-necessary DTOs per role (finance≠diagnosis, lab≠rx, pharmacy≠investigation-result); write DTOs allow-listed | Field-projection authz tests (e.g. finance cannot read diagnosis; reporting financial_fact has no diagnosis col) | Green |
| API4 | **Unrestricted resource consumption** | Kong rate-limit/quota per role+route; pagination allow-listed; async heavy analytics; HPA/KEDA | Kong config; perf suite (NFR-006) | Green (config) |
| API5 | **Broken function level authz** | OAuth2 scope per operation, checked at gateway + service (`HbmpPolicies.Scope`); SoD engine | Scope-policy tests; SoD matrix tests | Green |
| API6 | **Unrestricted access to sensitive business flows** | Consume/dispense require `Idempotency-Key`; approvals decisions mandatory-rationale; break-glass time-boxed+audited | Concurrency/consume tests; break-glass drill | Green |
| API7 | **SSRF** | No user-supplied URLs fetched server-side; cross-service seams call fixed in-cluster hosts only (forward caller bearer, never a caller URL) | Code review of HTTP seams (callcentre/case/finance gateways) | Green |
| API8 | **Security misconfiguration** | ModSecurity CRS blocking; TLS 1.3+HSTS; default-deny NetworkPolicies; RFC7807 errors; no verbose stack traces | Trivy config scan (`security-ci`); TLS scan | Gate wired; staging scan pending |
| API9 | **Improper inventory management** | Single versioned `/api/v1` surface; OpenAPI as source of truth; Kong is the only public origin | Kong route inventory; OpenAPI specs | Green |
| API10 | **Unsafe consumption of 3rd-party APIs** | External integrations (PBM/formulary stub, courier n/a) validated + treated as untrusted; no auto-trust | Code review; integration stubs | Green |

## BOLA sweep method (the headline)

For every `role × resource` pair in the min-necessary matrix (`11-*`), assert:
1. **Row**: a caller without the ABAC relationship gets default-deny (not another tenant's/
   provider's/branch's row).
2. **Field**: even when the row is visible, disallowed fields are absent from the response
   (not merely hidden in UI) — verified over serialized JSON.

Automated: the per-service authz test suites already encode these pairs; the release gate runs
them all green. Any new endpoint MUST add its row+field pair before merge.

## Open items → sign-off
- API8 staging TLS scan + ModSecurity false-positive tuning: pending stable staging (job wired).
- Authenticated ZAP full-scan (active): pending staging origin.
