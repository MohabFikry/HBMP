# Security Sign-Off — Mersal HBMP (Phase 11.2)

Pre-prod security gate per `20-compliance-checklist.md` + `18-security-model.md`. Each line is
**Pass + evidence** or **Pending + owner**. A release is **not shippable** while any line is
Fail or an unretested High/Critical finding is open (NFR §15). Signed by Security/DPO.

Legend: ✅ Pass · 🟡 Pending (harness/gate wired; needs staging or an external run) · ❌ Fail

| # | Gate | Control / evidence | Status |
|---|---|---|---|
| SEC-STRIDE | STRIDE threat model current | `docs/security/threat-model-stride.md` (edge + per-context + data tier; residual risks → backlog) | ✅ |
| SEC-SAST | SAST clean (no Critical/High) | CodeQL (C#) + Trivy config scan in `security-ci.yml`, `exit-code:1` on Crit/High | 🟡 first run on CI |
| SEC-SCA | Dependency scan clean | Trivy fs (SCA), `ignore-unfixed`, Crit/High block | 🟡 first run on CI |
| SEC-IMAGE | Container image scan clean | Trivy image scan per service (added to release pipeline) | 🟡 needs built images |
| SEC-DAST | DAST against staging | OWASP ZAP baseline + authenticated full-scan (`security-ci` dast-zap job) | 🟡 needs staging origin |
| SEC-SECRETS | 100% secrets outside code | gitleaks gate (`.gitleaks.toml`); repo verified — all runtime secrets are `${VAR}` env-injected from gitignored `.env`; **prod target: OpenBao/Vault + SOPS**, per-env namespaces, prod keys unreachable from lower envs | ✅ code-clean / 🟡 OpenBao migration |
| SEC-AUTHZ | OWASP API Top 10 + row/field authz | `docs/security/owasp-api-top10-checklist.md`; per-service authz suites (min-necessary row+field) green; BOLA sweep per role×resource | ✅ (suite green) |
| SEC-TLS | TLS 1.2+ (prefer 1.3) + HSTS everywhere | Ingress TLS + HSTS; TLS scan | 🟡 staging TLS scan |
| SEC-MTLS | Service-to-service mTLS | Linkerd mesh | 🟡 verify in k3s |
| SEC-REST | Encryption at rest | LUKS full-disk + pgcrypto (PHI cols) + MinIO SSE; AES-256 keys in OpenBao transit + rotation policy | 🟡 verify in target infra |
| SEC-WAF | WAF in blocking mode, tuned | ModSecurity + OWASP CRS at ingress; false-positive tuning w/o disabling coverage | 🟡 staging tuning |
| SEC-RATE | Rate-limit / quota per role/route | Kong rate-limiting plugin (global net + per-route); Keycloak brute-force lockout | ✅ config (kong.yml) / 🟡 per-route tuning |
| SEC-NETPOL | k3s default-deny NetworkPolicies | Least-privilege per-service; negative connectivity test (pod cannot reach data tier out of policy) | 🟡 needs k3s |
| SEC-CLAMAV | Upload malware scan before access | ClamAV on document ingest | 🟡 verify wired on ingest path |
| SEC-PENTEST | External pen test + retest High/Crit | Commissioned against staging; report + retest in `docs/security/` | 🟡 external engagement |
| SEC-BREAKGLASS | Break-glass time-boxed + immutably audited + auto-revoked + **runtime elevation** | Admin-service lifecycle (dual-control, step-up MFA, scoped auto-expiring window, HIGH audit) + 16.6/H5 runtime: every service's `HttpBreakGlassProvider` reads admin `/break-glass/active` (caller token forwarded, 30s cache, FAIL-CLOSED) so a grant actually widens access at decision time — the engine allows the otherwise-denied read with HIGH audit + ends it on expiry (AuthorizationEngineTests, HttpBreakGlassProviderTests). Prior ✅ was an overclaim (NullBreakGlassProvider was wired everywhere ⇒ grants never elevated). | ✅ (built+tested) / 🟡 live cross-service e2e drill on staging |
| SEC-AUDIT-WORM | Audit hash-chain + WORM durability | Append-only hash-chained `audit_event`; MinIO object-lock; survives failover (see DR drill 11.3) | ✅ chain / 🟡 WORM in target infra |

## Acceptance (Given/When/Then)
- CI security gates: any Critical/High vuln or exposed secret ⇒ pipeline blocks release. **Wired** (`security-ci.yml`).
- Any endpoint: a role requesting an object it may not see ⇒ default-deny, no unpermitted field leaks. **Proven by authz suite (✅).**
- Break-glass: invoked ⇒ time-boxed, justified, immutably audited, auto-revoked. **Built + tested (✅); prod drill 🟡.**

## What "green" requires beyond this repo
The ✅ items are enforced in code/CI/tests now. The 🟡 items are **operational gates** that
require the target infrastructure (k3s cluster, staging origin, OpenBao, external pen-test
vendor). Each has its gate/harness wired here; sign-off completes when they run against that
infrastructure. This document is the single checklist the go-live gate (phase 12) consumes.

## Signatures
| Role | Name | Date | Decision |
|---|---|---|---|
| Security owner | | | |
| DPO | | | |
