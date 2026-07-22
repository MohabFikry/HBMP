# Phase 0 — Platform Foundations (Release R0)

**Goal:** stand up the monorepo, IaC dev environment, CI/CD, identity + MFA, the **audit spine**, the **RBAC/ABAC engine**, and a reusable service template + gateway — the shared substrate every later service depends on.

Back to [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Design set: [../0A-DESIGN-FOUNDATIONS.md](../0A-DESIGN-FOUNDATIONS.md) · [../16-service-architecture.md](../16-service-architecture.md) · [../18-security-model.md](../18-security-model.md) · [../19-audit-strategy.md](../19-audit-strategy.md) · [../25-deployment-architecture.md](../25-deployment-architecture.md)

> Build the substrate before the features. Nothing in later phases ships without the audit client and the authz library wired in. Build a thin vertical slice (one "hello" service deployed to dev, logged-in, audited, authorized) before widening.

## Skills to activate
> Activate `healthcare-database-architect` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [../0A-DESIGN-FOUNDATIONS.md](../0A-DESIGN-FOUNDATIONS.md) — key formats, ID schemes (`MRS-M-*`, `ENC-*`, `AUTH-*`), UUID v7, defaults to assume when unspecified.
- [../16-service-architecture.md](../16-service-architecture.md) — bounded contexts, service boundaries, outbox, sagas, schema-per-service.
- [../18-security-model.md](../18-security-model.md) — Zero Trust, enforcement points (gateway/service/row/field), ABAC attributes, break-glass, encryption, key management.
- [../19-audit-strategy.md](../19-audit-strategy.md) — audit event schema (§3), immutability + hash-chaining + WORM (§4), correlation (§5), isolation (§10), event catalog (§11).
- [../25-deployment-architecture.md](../25-deployment-architecture.md) — k3s/Compose, Kong, OpenBao (CMK), PostgreSQL, RabbitMQ/NATS, Valkey, environments and IaC layout.
- [../0C-OPEN-SOURCE-STACK.md](../0C-OPEN-SOURCE-STACK.md) — authoritative free/open-source, on-prem-first, cloud-ready stack decision and the Azure→OSS mapping every infra choice follows.
- [../22-data-dictionary.md](../22-data-dictionary.md) §10.4 — `audit.audit_event` table shape. [../10-role-matrix.md](../10-role-matrix.md), [../11-permission-matrix.md](../11-permission-matrix.md) — roles, field-class access.

The root `CLAUDE.md` already carries the full stack, naming, security, audit, a11y, testing rules and Definition of Done. Do **not** restate them; apply them.

## Prompts

### 0.1 — Monorepo scaffold, CI/CD, and dev IaC

```text
Read ../16-service-architecture.md, ../25-deployment-architecture.md, and ../0C-OPEN-SOURCE-STACK.md, plus the repository layout in the root CLAUDE.md.
The infrastructure is a **free, open-source, on-prem-first, cloud-ready** stack ($0 licensing — Mersal is a charity). Backend stays **.NET 8**; only infra product names change per the ../0C Azure→OSS mapping. Deployment tiers: Tier 1 = single server via **Docker Compose** ($0); Tier 2 = on-prem **k3s**; Tier 3 = cloud lift-and-shift with the **same Helm charts**.

Scaffold the monorepo skeleton exactly as CLAUDE.md defines:
  /services  /apps  /libs  /infra  /docs  /tools
Add a solution-wide Directory.Build.props (nullable enable, warnings-as-errors, langversion), .editorconfig, Directory.Packages.props (central package management), a .gitignore, and a top-level README pointing to /docs and the design set.

Create the CI/CD pipeline on **GitLab CE** (or **Gitea + Woodpecker** — pick one and note the choice in an ADR), pushing images to **Harbor** (self-hosted registry), with these stages, each a required gate:
  - restore + build (all services and libs)
  - unit + contract tests (Pact placeholder), coverage gate >= 80% on /libs and /services domain projects
  - SAST (e.g., Semgrep) — fail on high/critical
  - dependency + container image scan (**Trivy**) — fail on high/critical
  - a11y hook: reserve a job that runs axe against /apps builds (no-op until phase 9, but wired now)
  - IaC validation: **OpenTofu validate + plan** and Helm/Ansible lint
Enforce Conventional Commits via a commit-lint check.

Author the IaC skeleton under /infra using **OpenTofu + Ansible + Helm** for a single **dev** environment, targeting a local **k3s** cluster (or **Docker Compose** for the single-node Tier 1 path), provisioning at minimum:
  PostgreSQL (schema-per-service, `pgcrypto`; Patroni HA + pgBackRest ready), **Keycloak** (IdP), **Kong** (API gateway) behind **Traefik/NGINX Ingress + ModSecurity/OWASP CRS + Let's Encrypt**, **MinIO** (S3-compatible object store), **RabbitMQ** + **NATS JetStream** (queues/events), **Valkey** (cache), **OpenSearch** (search), **OpenBao** (secrets/KMS), and the **OpenTelemetry + Prometheus + Grafana + Loki + Tempo** (LGTM) observability stack.
Parameterize per-env and per-tier; apps read secrets from **OpenBao** (with **SOPS**-encrypted values in git) — put NO plaintext secrets in the repo. Store OpenTofu state/backend config as documented, not committed.

Write an ADR in /docs/adr for each real decision (CI/CD platform choice, IaC tooling, deployment-tier strategy, network topology).

Acceptance criteria:
  - `tofu plan` succeeds for dev with zero hardcoded secrets; the same Helm charts target both k3s and (documented) cloud.
  - CI runs green on an empty scaffold; SAST, scan, coverage, and IaC-validate gates are present and required.
  - Repo layout matches CLAUDE.md; ADRs exist for each decision.
Applies to: platform enablement (NFR-Deployability, NFR-Security). No user-facing story yet.
```

### 0.2 — Identity & access (Keycloak, OIDC/OAuth2, MFA) + `libs/auth`

```text
Read ../18-security-model.md (§ authentication, sessions, MFA, token validation), ../0C-OPEN-SOURCE-STACK.md, and the Identity section of CLAUDE.md.

Integrate **Keycloak** (self-hosted, on-prem-first) as the OIDC/OAuth2 identity provider. Deliver:
  - `libs/auth`: a shared .NET library that validates JWT access tokens (issuer, audience, signature via JWKS, expiry, nonce), exposes the authenticated principal (sub, roles, tenant_id, provider_id, session_id, acr/amr, mfa flag, src_ip), and surfaces OAuth2 scopes.
  - Enforce **MFA** (require an acr/amr indicating MFA; reject tokens without it for protected scopes) and support step-up (Keycloak authentication flows / required actions).
  - Session timeout with a client-visible warning contract, and token refresh/revoke handling.
  - Token validation happens at the **API gateway (Kong) AND at each service** (defense in depth) — provide the Kong (OIDC/JWT) plugin config and the service-side middleware.
  - Define the OAuth2 scope catalog seed (role → scopes, e.g. `orders:consume`, `auth:decide`) as config, source-controlled; provision Keycloak realm/clients/roles as code (import JSON or Terraform provider).

Emit auth audit events (login success/failure, mfa challenge/result, token issue/refresh/revoke, logout, lockout) through the audit client from 0.3 once available; stub the call behind an interface until then.

Acceptance criteria:
  - A request with a valid MFA-backed token passes; a token without MFA is rejected for a protected scope with RFC 7807 problem+json.
  - Expired/invalid-signature/wrong-audience tokens are rejected at both gateway and service.
  - `libs/auth` has unit tests covering each rejection path; principal exposes all ABAC-relevant claims.
Applies to: US-AUTH-* (login, MFA), NFR-Security (authentication, session management).
```

### 0.3 — Audit spine: `audit-service` + `libs/audit-client` (FOUNDATIONAL)

```text
Read ../19-audit-strategy.md in full (esp. §3 schema, §4 immutability/hash-chaining/WORM, §5 correlation, §10 isolation, §11 event catalog) and ../22-data-dictionary.md §10.4.

Everything else depends on this. Build the immutable audit trail.

Build `audit-service` (its own bounded context, own schema `audit`, own managed identity, private endpoints):
  - `audit.audit_event` append-only table: audit_event_id (uuid v7), service_name, entity_type, entity_id, action (CREATE|UPDATE|SOFT_DELETE|STATE_CHANGE|CONSUME|DISPENSE|DECISION|READ|LOGIN|GRANT|EXPORT|DECISION), actor_user_id, before_state jsonb (minimized), after_state jsonb (minimized), correlation_id, occurred_at, plus severity, source_service, actor context (role, tenant_id, provider_id, session_id, mfa/acr), decision (outcome/policy_id/conditions/reason_code), purpose, break_glass, field_classes, prev_hash, record_hash. Partition monthly. Index (entity_type, entity_id, occurred_at) and (correlation_id).
  - **Immutability:** grant service identities INSERT only — no UPDATE/DELETE within retention (DB grants + RLS). Persist a copy to **MinIO** with object-lock/WORM (time-based retention + legal-hold) as the tamper-evident store.
  - **Hash-chaining:** each record carries prev_hash (previous record in its partition) and record_hash (sha256 of the canonicalized record excluding record_hash). Provide a canonicalization function.
  - **Periodic anchoring + verifier:** a scheduled integrity job re-computes the chain, compares against signed checkpoints, and raises a `integrity.mismatch` critical alert on any break.
  - Ingest **only** via RabbitMQ (guaranteed at-least-once delivery, dedupe on event_id). No synchronous write path from business services.

Build `libs/audit-client`: a shared library every service uses to emit audit events. It must:
  - populate correlation_id from W3C Trace Context (traceparent) automatically,
  - minimize before/after snapshots (no raw PHI values; capture field_classes),
  - be fire-and-forget durable (write to the emitting service's outbox, relayed to RabbitMQ) so an audit emit cannot be lost or block the business transaction incorrectly,
  - refuse to compile-out in production (no silent no-op).

Audit reads are themselves audited (`audit.read`, `audit.export`). Only Security/Compliance/DPO roles may read audit.

Acceptance criteria:
  - Emitting an event via `libs/audit-client` results in a chained, WORM-persisted record with correct prev_hash/record_hash.
  - Attempting UPDATE or DELETE on `audit.audit_event` is denied at the DB layer (test proves it).
  - The verifier detects a deliberately tampered record and raises a critical alert.
  - A read of the audit store emits its own `audit.read` event.
  - Correlation id propagates from an inbound request through to the audit record.
Applies to: NFR-Auditability, HIPAA §164.312(b), GDPR Art. 5(2)/30/32. Invariant #3 (immutable hash-chained audit).
```

### 0.4 — AuthZ engine: `libs/authz` (RBAC + ABAC, row + field level)

```text
Read ../18-security-model.md (enforcement points, ABAC attributes, break-glass, default-deny), ../10-role-matrix.md, and ../11-permission-matrix.md (row + field-class rules).

Build `libs/authz`, the mandatory authorization library for every service, backed by a policy engine (OPA or Cerbos — choose one, ADR it, run as a sidecar/local service).
  - **RBAC:** map authenticated roles + OAuth2 scopes to coarse permissions.
  - **ABAC:** evaluate fine-grained policies over attributes: treating-relationship (doctor ↔ beneficiary), provider-ownership (order/rx belongs to the caller's provider), tenant, resource status (order/rx/authorization status), and break-glass grant.
  - **Default-deny.** Every decision returns allow/deny + reason_code + satisfied condition codes, and emits an `authz.deny` (and allow on sensitive resources) audit event via `libs/audit-client`.
  - **Row-level primitive:** a queryable predicate/spec the data layer composes into SQL (aligns with PostgreSQL RLS) so callers only see rows they may see.
  - **Field-level primitive:** a projection/DTO-shaping helper that strips field-classes the caller may not read (e.g., reception must not receive diagnosis; labs must not receive prescriptions; pharmacies must not receive investigation results; finance must not receive diagnoses). Minimum-necessary is enforced in code, not comments.
  - **Break-glass:** support a scoped, time-boxed, dual-reviewed grant that widens access and forces high-severity audit on every read under the grant.
  - Ship policy bundles as versioned, source-controlled artifacts deployed through CI (emit `admin.policy.deploy` audit on deploy).

Acceptance criteria:
  - A policy denies a field the caller may not read: the returned DTO omits that field-class AND an `authz.deny`/field-strip is audited (authorization test proves it).
  - Row predicate limits a doctor to beneficiaries they treat; a cross-provider read is denied.
  - Break-glass access is time-boxed and every read under it is audited at high/critical severity.
  - Default-deny: an unmapped action is denied.
Applies to: Invariant #2 (minimum-necessary, row+field), ../11-permission-matrix.md, US-SEC-* authorization stories.
```

### 0.5 — Service template + Kong gateway + error model + `libs/events` + observability

```text
Read ../16-service-architecture.md (service anatomy, outbox, events), ../25-deployment-architecture.md (Kong, ingress), ../0C-OPEN-SOURCE-STACK.md, and the API conventions in CLAUDE.md.

Deliver a reusable `dotnet` service template plus the shared plumbing:
  - **Service template** (`/tools/templates` or a `dotnet new` template) producing a service with projects `Api / Domain / Infrastructure / Tests` and a `README.md` from the template in ../34-technical-documentation.md. It pre-wires: `libs/auth`, `libs/audit-client`, `libs/authz`, `libs/events`, OpenTelemetry, health/readiness probes, EF Core/Dapper with schema-per-service + migrations (expand/contract), and an OpenAPI 3.1 doc endpoint. Ship a Dockerfile + Helm chart so the service runs identically on Docker Compose (Tier 1) and k3s (Tier 2/3).
  - **API gateway (Kong OSS) config** (in /infra): routing, JWT/OIDC validation (against Keycloak), rate limiting, per-role scope checks, correlation-id (traceparent) injection/propagation, fronted by **Traefik/NGINX Ingress + ModSecurity (OWASP CRS) + Let's Encrypt TLS**; service-to-service mTLS via **Linkerd**. Keep OpenAPI as the source of truth.
  - **Error model:** a shared RFC 7807 `application/problem+json` handler with a canonical problem-type catalog.
  - **`libs/events`:** transactional **outbox** — publish domain events in the same DB transaction as the state change, relay to **RabbitMQ** (ordered domain events) / **NATS JetStream** (lightweight fan-out, CloudEvents); idempotent consumers (dedupe by event id); helper for sagas. Event naming `<Domain><PastTenseVerb>`.
  - **Observability baseline:** OpenTelemetry traces/metrics/logs exported to the **LGTM stack — Prometheus (metrics) + Grafana (dashboards) + Loki (logs) + Tempo (traces)**, correlation id shared with audit (but stored separately), and a starter dashboard + alert rules.
  - Scaffold a trivial `hello-service` from the template to prove the vertical slice end to end.

Acceptance criteria:
  - `dotnet new hbmp-service` produces a buildable service with all four libs wired and a green test project.
  - `hello-service` deploys to dev via the 0.1 pipeline + IaC (Compose or k3s); an authenticated MFA request routes through Kong, is authorized by `libs/authz`, performs one audited action, and returns problem+json on error.
  - A domain event published via `libs/events` lands on RabbitMQ/NATS and a duplicate delivery is deduped.
  - Traces for a request are visible in Grafana/Tempo and share the audit correlation id.
Applies to: NFR-Observability, NFR-Consistency (outbox), platform enablement.
```

## Guardrails

- **`libs/audit-client` and `libs/authz` are mandatory dependencies of every future service.** A service that mutates state without emitting hash-chained audit, or that returns data without field-level minimization, is not done.
- Immutable, append-only, hash-chained audit on all mutations; audit reads are themselves audited; audit service is isolated (own identity, WORM, no update/delete within retention).
- Default-deny authorization; minimum-necessary enforced at row **and** field level, in code.
- No secrets in code or repo — **OpenBao/Vault** (with SOPS-encrypted values in git) only.
- Migrations are backward-compatible (expand/contract). Soft-delete + `_history`; never hard-delete clinical/benefit data.
- Idempotency keys accepted on mutating endpoints; outbox used for all domain-event publication.
- MFA required for protected scopes; tokens validated at gateway **and** service.

## Done when

- The CI/CD pipeline deploys the `hello-service` through IaC to the **dev** environment (build → tests → SAST → scan → IaC → deploy).
- A user can **log in with Keycloak + MFA**; a non-MFA token is rejected for a protected scope.
- An **audited action is visible** as an immutable, hash-chained record (and a DB update/delete on the audit table is proven to be denied; the verifier catches a tamper).
- An **authz policy denies an unauthorized field** — the response DTO omits the disallowed field-class and the denial is audited.
- Service template, Kong gateway config, RFC 7807 error model, `libs/events` outbox, and OpenTelemetry + LGTM observability baseline exist and are exercised by the vertical slice.
- ADRs recorded for CI/CD platform, IaC tooling, deployment-tier strategy, and policy engine choices.
