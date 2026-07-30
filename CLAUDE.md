# Root `CLAUDE.md` — Mersal HBMP conventions & standards

> These conventions are loaded automatically every session and apply to every phase prompt without repetition. The full design set lives in `HBMP-Design/`. **Always read the referenced design docs before implementing.** The phased build prompts live in `HBMP-Design/claude-code-prompts/` (start at `00-MASTER-PROMPT-LIST.md`).

---

## Project: Mersal Healthcare Benefit Management Platform (HBMP)

A service-oriented benefit-administration + EMR platform for refugee beneficiaries of Mersal Foundation. Reusable core (Beneficiaries, Eligibility, Coverage/Policy, Provider Network, Authorizations, Orders, Prescriptions) with clinical/operational domains on top. Full design set lives in `HBMP-Design/`. **Always read the referenced design docs before implementing.**

## Project skills (use them)
This project ships 20 custom domain skills in `HBMP-Design/claude-code-skills/` (index: `00-SKILLS-INDEX.md`), installed under `.claude/skills/`. They encode Mersal's benefit rules, state machines, minimum-necessary zoning, and brand system — knowledge generic skills lack.

- **At the start of every phase session, activate the skills listed for that phase** in `HBMP-Design/claude-code-skills/00-SKILLS-INDEX.md` (Phase → skills mapping). Each phase prompt file also names its skills.
- **Always-on:** `mersal-platform-architect` (architecture discipline) and `refugee-healthcare-management` (privacy & minimum-necessary) apply to all work.
- For any UI work also use `healthcare-uiux-designer`; for any schema/migration use `healthcare-database-architect`; for any benefit/auth/formulary rule use `healthcare-business-rules-engine`.
- Generic engineering skills (PostgreSQL, OpenAPI, Terraform, OWASP, TDD, Mermaid, etc.) should be installed from a marketplace — the 20 custom skills cover *Mersal's rules*, the generics cover *how to build*.

## Tech stack (baseline)
**Open-source, on-prem-first, cloud-ready, $0 licensing.** Mersal is a charity: everything self-hostable on one server → k3s → any cloud, same containers. Authoritative details + Azure→OSS mapping + security parity: `HBMP-Design/0C-OPEN-SOURCE-STACK.md`.
- **Backend:** .NET 8 (C#) microservices (MIT, Linux). One service per bounded context.
- **Frontend:** React + TypeScript (Vite), Radix UI primitives + custom Mersal theme, `i18next` (Arabic RTL + English). Design system per `HBMP-Design/0B-DESIGN-SYSTEM-UI.md`.
- **Data:** PostgreSQL (schema-per-service), Row-Level Security; at-rest via LUKS + pgcrypto; HA via Patroni; PITR via pgBackRest. EF Core or Dapper.
- **API:** REST, versioned `/api/v1`, OpenAPI 3.1, FHIR R4-aligned where practical.
- **Async:** RabbitMQ (commands/queues) + NATS JetStream or Redpanda (domain events); CloudEvents; **transactional outbox**.
- **Identity:** in-app **identity-service** — ASP.NET Core Identity + OpenIddict (OIDC/OAuth2, MFA TOTP) — issues the frozen token contract (`docs/security/token-contract.md`); replaced Keycloak in Phase 17 (ADR-0015). **AuthZ:** RBAC + ABAC via OPA/Cerbos (or OpenFGA) + PostgreSQL RLS.
- **Infra:** k3s + Helm (Docker Compose single-node); Kong Gateway (OSS); Traefik/NGINX Ingress + ModSecurity (OWASP CRS) + Let's Encrypt; MinIO (S3-compatible, SSE, object-lock/WORM); Valkey cache; OpenSearch; OpenBao/Vault (KMS/secrets) + SOPS; Linkerd mTLS; OpenTelemetry + Prometheus + Grafana + Loki + Tempo. IaC via OpenTofu + Ansible + Helm.
- **CI/CD:** GitLab CE (or Gitea + Woodpecker) + Harbor; Trivy scans; ClamAV on uploads. **DR:** pgBackRest + Velero + restic, offsite (RPO≤15m/RTO≤2h).
- Cloud-ready = containers + Kubernetes + S3-compatible + OIDC + OTel; migrating to managed cloud swaps infra, not code.
- Full rationale: `HBMP-Design/0C-OPEN-SOURCE-STACK.md`, `16-service-architecture.md`, `25-deployment-architecture.md`.

## Repository layout (monorepo)
```
/CLAUDE.md
/docs/                      # ADRs, runbooks (see 34-technical-documentation.md)
/infra/                     # OpenTofu + Ansible + Helm, per-env (k3s/Compose)
/libs/                      # shared: contracts, auth, audit-client, events, testing
/services/
  identity/  patient/  policy/  eligibility/  emr/  orders/
  approvals/ provider/ pharmacy/ notification/ reporting/ audit/ document/ masterdata/
/apps/
  web/                      # React portals (role-based code-split)
  design-system/            # tokens + component library
/tools/                     # data loaders (master data ingestion)
```
Each service: `Api/ Domain/ Infrastructure/ Tests/` + `README.md` (template in `34-technical-documentation.md`).

## Naming & conventions
- Services `<domain>-service`; DB schema `<domain>`; events `<Domain><PastTenseVerb>` (e.g., `OrderLineConsumed`).
- REST resources plural kebab: `/beneficiaries`, `/investigation-orders`, `/authorizations`.
- Surrogate keys `uuid` (v7). Business keys: `MRS-M-*`, `ENC-*`, `ORD-*`, `RX-*`, `AUTH-*`, `REF-*` (see `0A §3`).
- Timestamps `timestamptz` UTC; display `Africa/Cairo`.
- Enums exactly as in `22-data-dictionary.md §11` and `23-state-machines.md`.
- C#: nullable enabled, records for DTOs, `Result<T>` over exceptions for expected failures. TS: strict mode, no `any`, zod for runtime validation.

## API conventions
- Versioned `/api/v1`. Pagination `?page&pageSize` (or cursor). Filtering explicit allow-list.
- Errors: RFC 7807 `application/problem+json`.
- **Idempotency:** mutating endpoints that must not double-apply accept `Idempotency-Key`; consume/dispense require it.
- OAuth2 scopes per role (e.g., `orders:consume`, `auth:decide`). Validate at gateway *and* service.
- Every response returns only min-necessary fields for the caller's role (see security below). Document with OpenAPI; keep spec as source of truth (`17-api-specifications.md`).

## Events & consistency
- Publish domain events via **outbox** in the same transaction as the state change; relay to RabbitMQ (queues) / NATS JetStream or Redpanda (event stream).
- Consumers are idempotent (dedupe on event id). Use sagas for multi-service workflows (order→approval, prescription→approval).
- Eligibility snapshots cached in Redis, invalidated by policy/coverage events.

## Security (must-haves — `18-security-model.md`)
- **Zero Trust, least privilege, need-to-know.** Default-deny.
- **RBAC + ABAC**: enforce at gateway (coarse), service (scope), and **row + field level** (fine). ABAC attributes: treating-relationship, provider-ownership, tenant, order/rx status, break-glass.
- **Minimum-necessary is code, not comments:** field-level projections/DTOs per role. Reception≠EMR; labs≠prescriptions; pharmacies≠investigation results; finance≠diagnoses; doctors see only assigned patients; approval team can see EMR/notes/reports.
- Encryption: TLS 1.2+/mTLS (Linkerd) in transit; AES-256 at rest via LUKS full-disk + pgcrypto (PHI/PII columns) + MinIO SSE; keys in OpenBao/Vault (transit engine), rotated; no secrets in code (Kubernetes ServiceAccount/workload identity + OpenBao + SOPS).
- MFA, session timeout with warning, password policy, IP allow-lists for provider/admin, provider & tenant isolation.
- Validate all input; follow OWASP API Top 10; rate-limit at gateway.

## Audit (must-have — `19-audit-strategy.md`)
- Every create/update/state-change/decision/**consume**/**dispense**/export/PHI-read writes an **immutable, append-only, hash-chained** `audit_event` via the shared audit client. Include actor, entity, before/after (minimized), correlation id, timestamp. Audit is itself protected and its reads are audited. Never hard-delete clinical/benefit data (soft delete + `*_history`).

## Accessibility & i18n (must-have — `21-accessibility-checklist.md`, `0B`)
- WCAG 2.2 AA. Keyboard-operable, visible 3px focus, ≥44px targets, non-color status (hue+icon+shape+text), AA contrast against composited backgrounds, `aria-live` for async outcomes.
- Full **Arabic RTL + English**; layout mirrors. Use the confirmed Mersal palette and the design tokens/components from `0B`; use the official Mersal logo (white mark on teal tile) with text fallback.

## Testing (must-have — `26-testing-strategy.md`)
- Unit + integration + **contract tests** (Pact) for APIs/events + E2E for critical flows.
- **Authorization tests** proving min-necessary (e.g., finance cannot read diagnosis; lab cannot read prescriptions).
- **Concurrency tests** proving order-consume atomicity/no-reuse under parallel requests.
- Accessibility: axe in CI (fail on serious/critical) + keyboard/screen-reader checks per UI story.
- Coverage: the numbers live in `tools/ci/coverage-floors.json` and nowhere else — target, enforced
  floors and per-module floors together. Three files once claimed three different bars and only one was
  enforced, so "what is the coverage bar?" had three answers depending on which you opened. The domain
  TARGET is 80%; the enforced floor is lower and ratchets upward only (`check-floor-monotonicity.py`).
  Test data synthetic/masked; never real PHI in lower envs.

## Definition of Done (every prompt/PR)
- [ ] Meets the acceptance criteria / user story (`32-user-stories.md`).
- [ ] Min-necessary field rules enforced and tested.
- [ ] Audit events written for all mutations; no hard deletes.
- [ ] Tests (unit/integration/contract/E2E as relevant) green; concurrency/authz tests where applicable.
- [ ] a11y gate passes (axe + keyboard + AR/RTL) for UI.
- [ ] OpenAPI updated; service README/ADR updated; runbook if operational.
- [ ] No secrets committed; migrations backward-compatible (expand/contract).
- [ ] Conventional commit + reviewable PR.

## Commit convention
`<type>(<scope>): <summary>` — types: feat, fix, chore, refactor, test, docs, perf, sec. Scope = service/app. Reference story id (US-xxx) and phase.

## Ground rules for Claude Code
- Read the design docs named in the prompt **before** coding; if reality diverges from a doc, flag it, don't silently deviate.
- Build the thin vertical slice first; keep services independently deployable.
- Prefer clarity over cleverness; small PRs; write the test with the code.
- When a decision isn't specified, follow `0A` defaults and note the assumption in the PR.

## Build environment notes (this machine)
- .NET 8 SDK is installed **user-local** at `~/.dotnet` (no system install). Ensure `PATH` includes `~/.dotnet` and set `DOTNET_ROOT=~/.dotnet`, or use `./dotnet.sh` if provided at repo root.
- Docker is required to run the Tier 1 infra (`infra/compose`) and needs root to install — see `docs/adr/0003-deployment-tier-strategy.md`.
- Local Postgres 17 is available for quick DB work; the canonical dev DB is the Compose `postgres:16`.
- **Running the DB-gated tests: `./dotnet.sh test --with-db <target>`.** ~100 integration, concurrency and RLS
  tests are gated on `Skip.If(<SERVICE>_TEST_DB is null)`, so a plain `dotnet test` skips every one of them and
  still reports green — the consume/dispense concurrency proofs, the RLS isolation suites and the break-glass
  lifecycle among them. `--with-db` points them at the Compose Postgres (`:55432`) using
  `tools/ci/print-test-db-env.sh`, the same variable list CI exports. It fails loudly if the DB is unreachable
  rather than letting a run skip everything quietly. **A green suite is only meaningful with this flag.**
