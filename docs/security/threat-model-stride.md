# STRIDE Threat Model — Mersal HBMP (Phase 11.2)

Refreshed per `18-security-model.md`. Scope: the edge (ingress ModSecurity/OWASP CRS → Kong
→ k3s), each bounded context, and the data tier. Each threat records mitigation and residual
risk; gaps become backlog items in `docs/security/`. Bar per NFR §4 SEC; default-deny,
least-privilege, need-to-know.

## Trust boundaries

```
Internet ─▶ [Ingress: TLS 1.3 + HSTS + ModSecurity/OWASP CRS]
         ─▶ [Kong: authN edge, rate-limit/quota, correlation-id]
         ─▶ [k3s pod network: default-deny NetworkPolicies, Linkerd mTLS]
         ─▶ services (each validates JWT + scope + ABAC row/field)
         ─▶ [Data tier: Postgres RLS + pgcrypto, MinIO SSE+object-lock, Valkey]  (in-cluster only)
```

## STRIDE per boundary

### Edge (ingress + Kong)
| STRIDE | Threat | Mitigation | Residual |
|---|---|---|---|
| **S**poofing | Forged/replayed JWT | Keycloak OIDC, short-lived tokens, audience+issuer validated at Kong *and* service; Linkerd mTLS between hops | Low |
| **T**ampering | Request smuggling / header injection | ModSecurity CRS in blocking mode; Kong normalises; correlation-id server-generated | Low |
| **R**epudiation | "I didn't make that call" | Hash-chained audit per action w/ actor+correlation-id (`19-audit-strategy`) | Low |
| **I**nfo disclosure | Verbose errors leak internals | RFC7807 problem+json, no stack traces to client; PHI-redacted logs | Low |
| **D**oS | Volumetric / credential-stuffing | Kong rate-limit/quota per role+route; Keycloak brute-force lockout; HPA/KEDA autoscale | Medium (volumetric needs upstream/CDN) |
| **E**oP | Bypass gateway to hit a service directly | k3s default-deny NetworkPolicies; services not on public data plane; negative connectivity test | Low |

### Services (bounded contexts)
| STRIDE | Threat | Mitigation | Residual |
|---|---|---|---|
| **S** | Service impersonation | Linkerd mTLS workload identity; per-service ServiceAccount | Low |
| **T** | Mass-assignment on write DTOs | Explicit record DTOs, allow-listed fields, zod/model validation | Low |
| **R** | Missing audit on PHI read/consume | Audit client mandatory on create/update/state-change/decision/**consume**/**dispense**/export/**PHI-read** | Low |
| **I** | **BOLA** (object-level) — reading another beneficiary/provider's row | ABAC row filter (treating-relationship, provider-ownership, tenant, branch) + Postgres RLS; **field-level** min-necessary projections per role (finance≠diagnosis, lab≠rx, pharmacy≠investigation-result, reception≠EMR) | Low — proven by authz suite |
| **D** | Poison message / hot loop in consumer | Idempotent consumers (dedupe on event id), DLQ, backoff | Low |
| **E** | Scope escalation / SoD bypass | Scope check at gateway+service; SoD engine (`libs/authz`), break-glass time-boxed+audited | Low |

### Data tier
| STRIDE | Threat | Mitigation | Residual |
|---|---|---|---|
| **T** | Direct DB tampering / audit rewrite | RLS; NO hard delete (soft-delete + `*_history`); append-only hash-chained audit; MinIO object-lock/WORM | Low |
| **I** | At-rest disclosure | LUKS full-disk + pgcrypto (PHI columns) + MinIO SSE; keys in OpenBao transit, rotated | Low |
| **E** | `hbmp_app` bypassing RLS | App role is `NOBYPASSRLS` (not superuser); verified in provider/emr isolation tests | Low |

## Key residual risks → backlog

1. **Volumetric DoS** at the edge needs an upstream scrubbing layer / CDN in the target
   deployment (out of scope for on-prem single-site) — track as ops risk.
2. **Secrets currently in host `.env`** (dev/compose). Prod target = OpenBao/Vault + SOPS with
   per-env namespaces and prod keys unreachable from lower envs — see sign-off item SEC-SECRETS.
3. **DAST active scan** (authenticated ZAP full-scan) requires a stable staging origin —
   scheduled job wired (`security-ci.yml`), first full run pending staging.

## Method

Reviewed context-by-context against the `18-security-model.md` threat catalogue; each public
endpoint cross-checked against the OWASP API Top 10 (`owasp-api-top10-checklist.md`). Findings
that are not already mitigated are filed as dated items here and retested before sign-off.
