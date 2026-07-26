# Mersal HBMP — Build Status & Execution Roadmap

Phases run in dependency order; **one sub-prompt ≈ one reviewable PR** (see `HBMP-Design/claude-code-prompts/00-MASTER-PROMPT-LIST.md`). Status: ☐ not started · ◐ in progress · ☑ done.

## Dependency order
`0 → 0b → 1 → 2 → 2b → 3 → 4 → 5,6 (parallel) → 14 (retrofit) → 7 → 8 → 10 → 10b`; `8b` + `9` run continuously from R0/R1; `11` gates go-live; `12` after 11; `13` after core. `10b` (claims) needs 5/6 fulfillment records, 2b contracts/tariffs, and 7 authorizations. **`14` (branch scoping & clinical sensitivity) is a cross-cutting retrofit of the built services and runs before `7` and `9`** — approvals must be built member-scoped and blind to sensitive results, and the frontend needs the branch switcher + restricted-result state. **`15` (call centre portal) is additive** and needs `3` (appointment engine), `2` (eligibility search), `1` (contacts), `8` (notifications) and `9` (design system/portal catalog) — all complete — so it can run now; it pairs with `14` (the Call Centre role is MemberScoped / all branches, i.e. `BranchUnrestricted` once 14 lands).

## ⇒ New here? Read docs/HANDOFF.md first (full continuation guide).

## Progress

| # | Phase | Sub-prompt | Status | PR/commit |
|---|-------|-----------|--------|-----------|
| 0 | Foundations | 0.1 Monorepo scaffold, CI/CD, dev IaC | ☑ | cac74b3 |
| 0 | Foundations | 0.2 Identity & access (Keycloak, MFA) + `libs/auth` | ☑ | (0.2) |
| 0 | Foundations | 0.3 Audit spine: `audit-service` + `libs/audit-client` | ☑ | (0.3) |
| 0 | Foundations | 0.4 AuthZ engine: `libs/authz` (RBAC+ABAC, row+field) | ☑ | (0.4) |
| 0 | Foundations | 0.5 Service template + Kong gateway + `libs/events` + observability | ☑ | (0.5) |
| 0b | Master Data | 0b.1 `masterdata-service` schema + read/search APIs | ☑ | (0b) |
| 0b | Master Data | 0b.2 Loaders: ingest real ICD-10/CPT/ATC drugs | ☑ | (0b) |
| 0b | Master Data | 0b.3 Interactions/allergens seed + validation endpoints | ◐ | (0b) |
| 1 | Registration | 1.1 `patient-service` | ☑ | (1.1) |
| 1 | Registration | 1.2 `policy-service` | ☑ | (1.2) |
| 1 | Registration | 1.3 `document-service` integration | ☑ | (1.3) |
| 1 | Registration | 1.4 Registration workflow + activation | ☑ | (1.4) |
| 2 | Eligibility | 2.1 `eligibility-service` + cache | ☑ | df91758 |
| 2 | Eligibility | 2.2 Reception search (min-necessary) | ☑ | 4d930ce |
| 2 | Eligibility | 2.3 Visit gating + encounter stub | ☑ | 15ea73e |
| 2b | Provider Network | 2b.1 `provider-service` | ☑ | (2b.1) |
| 2b | Provider Network | 2b.2 Onboarding workflow | ☑ | (2b.2) |
| 2b | Provider Network | 2b.3 Provider isolation (ABAC PO + RLS) | ☑ | (2b.3) |
| 3 | Appointments | 3.1 Appointment domain + slot booking | ☑ | a39b647 |
| 3 | Appointments | 3.2 Reschedule/cancel/no-show | ☑ | 19c0669 |
| 3 | Appointments | 3.3 Queue + reminders hook | ☑ | (3.3) |
| 4 | Clinical EMR | 4.1 `emr-service` + treating-relationship ABAC | ☑ | (4.1) |
| 4 | Clinical EMR | 4.2 `orders-service` + approval routing | ☑ | (4.2) |
| 4 | Clinical EMR | 4.3 `pharmacy-service` Rx + referral creation | ☑ | (4.3) |
| 5 | Lab/Imaging | 5.1 Provider order queue + search | ☑ | 0c88fec |
| 5 | Lab/Imaging | 5.2 Atomic idempotent consume | ☑ | 0c88fec |
| 5 | Lab/Imaging | 5.3 Result upload + routing | ☑ | 0c88fec |
| 6 | Pharmacy | 6.1 Dispensable search | ☑ | ce79500 |
| 6 | Pharmacy | 6.2 Partial dispensing (batch/expiry) | ☑ | ce79500 |
| 6 | Pharmacy | 6.3 Substitution + out-of-stock | ☑ | ce79500 |
| 14 | Branch & Sensitivity | 14.1 `branch` entity + seed the six branches | ☑ | d878aec |
| 14 | Branch & Sensitivity | 14.2 User↔branch assignment + active-branch context (`X-Active-Branch`) | ☑ | 9e2edc4 |
| 14 | Branch & Sensitivity | 14.3 `BranchScope` ABAC + `RowScope` + policy-bundle scope modes | ☑ | a1529ab |
| 14 | Branch & Sensitivity | 14.4 Branch-scope appointments/queue/orders worklists | ☑ | 2bfeedb/25e6f4e |
| 14 | Branch & Sensitivity | 14.5 Practitioner + specialty + doctor↔branch assignment | ☑ | f3e54bc |
| 14 | Branch & Sensitivity | 14.6 Examination type + sensitivity classification | ☑ | 27db413 |
| 14 | Branch & Sensitivity | 14.7 Sensitive-result gating + release-request workflow | ☑ | 25d3154 |
| 14 | Branch & Sensitivity | 14.8 Branch switcher + restricted-result UI | ☑ | 0f5a4bd |
| 15 | Call Centre | 15.1 `callcentre-service` (interaction + caller verification) | ☑ | bdcd50a |
| 15 | Call Centre | 15.2 Member search + min-necessary 360 | ☑ | 324bfc7 |
| 15 | Call Centre | 15.3 Book/reschedule/cancel from the call | ☑ | 533467a |
| 15 | Call Centre | 15.4 Contact updates + referrals/follow-ups | ☑ | eea308c |
| 15 | Call Centre | 15.5 Call Centre portal (frontend) | ☑ | 66feba8 |
| 15 | Call Centre | 15.6 KPIs, notifications + E2E | ☑ | (15.6) |
| 7 | Approvals | 7.1 `approvals-service` + worklist + review | ☑ | 15ba511 |
| 7 | Approvals | 7.2 Decisions + downstream effects | ☑ | a098550 |
| 7 | Approvals | 7.3 Break-glass + SLA/TAT | ☑ | d13a4d5 |
| 8 | Notify+Reporting | 8.1 `notification-service` | ☑ | 35604d3 |
| 8 | Notify+Reporting | 8.2 `reporting-service` KPI read-models | ☑ | 4768621 |
| 8 | Notify+Reporting | 8.3 Executive dashboard contracts | ☑ | 4768621 |
| 8b | Admin Platform | 8b.1 User/role admin + SoD + access review | ☑ | 70e8f17 |
| 8b | Admin Platform | 8b.2 Master-data/template/config admin | ☑ | e761ea3 |
| 8b | Admin Platform | 8b.3 Tenant/provider + break-glass governance | ☑ | 8107dc1 |
| 9 | Frontend | 9.1 Design system in code | ☑ | 9e38a22 |
| 9 | Frontend | 9.2 Role portals + permission routing | ☑ | a282450 |
| 9 | Frontend | 9.3 Flagship screens + `@mersal/contracts` | ☑ | 5197336 |
| 10 | Case + Finance | 10.1 `case-service` + beneficiary-360 | ☑ | c1e0c63 |
| 10 | Case + Finance | 10.2 `finance-service` (no-diagnosis) | ☑ | e23c2fd |
| 10 | Case + Finance | 10.3 Case + Finance portals | ☑ | (10.3) |
| 10b | Claims Mgmt | 10b.1 `claims-service` + auto-derived claims (no double-billing) | ☑ | 0278c0b |
| 10b | Claims Mgmt | 10b.2 Batching + batch lifecycle (single-open-batch) | ☑ | e68a64e |
| 10b | Claims Mgmt | 10b.3 Automated pre-adjudication (9-step, all reasons) | ☑ | 2323e2f |
| 10b | Claims Mgmt | 10b.4 Officer review + line-level decisions (SoD, dual control) | ☑ | 735021a |
| 10b | Claims Mgmt | 10b.5 Provider-submitted claims + document matching | ☑ | 6851e54 |
| 10b | Claims Mgmt | 10b.6 Beneficiary reimbursement + OCR (assistive) | ☑ | fc2d76a |
| 10b | Claims Mgmt | 10b.7 Reconciliation + append-only adjustments | ☑ | 2bac316 |
| 10b | Claims Mgmt | 10b.8 Settlement advice + exports (no payment execution) | ☑ | acf86eb |
| 10b | Claims Mgmt | 10b.9 Appeals + claims KPIs | ☑ | 7dc6ea7 |
| 11 | Hardening/NFR | 11.1 Perf/scale harness + baseline + index/cache ADR | ☑ | cac6e7a |
| 11 | Hardening/NFR | 11.2 STRIDE + CI security gates + OWASP API Top10 + sign-off | ☑ | c9d20c7 |
| 11 | Hardening/NFR | 11.3 Fleet metrics + dashboards/alerts + DR/restore + runbooks | ☑ | eb9c339 |
| 16 | Audit Remediation | 16.1 Secrets purge + doc reconciliation (C2) | ☑ | hbmp_app pw rotated + purged from git history; base-appsettings connstrings removed; ~14 code fallbacks fail-fast; gitleaks prose-password rule; doc reconciliation |
| 16 | Audit Remediation | 16.2 Durable transactional outbox (C1) | ☑ | EfOutbox + FOR UPDATE SKIP LOCKED reader + `outbox_message` per schema (16 svcs, live-applied) + env-driven default-durable + ADR-0013; 726 tests green |
| 16 | Audit Remediation | 16.3 Admin authz middleware + MFA + idempotency (C3) | ☐ | deferred → Phase 17.4 (MFA lands on the new in-app issuer; do not do twice) |
| 16 | Audit Remediation | 16.4 RLS everywhere as `hbmp_app` (H1) | ☑ | full multi-tenant retrofit — `tenant_id` on ~45 tables + history twins; shared `libs/data` binder + stamping interceptor; all 13 svcs → NOBYPASSRLS hbmp_app + ENABLE/FORCE RLS (live-applied); per-svc 2-role isolation tests; ADR-0011 |
| 16 | Audit Remediation | 16.5 Kong JWT + missing routes + transport hardening (H3/H8) | ◪ | non-identity portion DONE: +7 missing route groups; `/beneficiaries` collision fixed via regex routes→emr/eligibility/document (live-verified); route-coverage CI guard (`tools/ci/check-kong-route-coverage.py`); CORS If-Match/ETag/credentials:false; `RequireHttpsMetadata:false` base→Development; shared `UseHbmpTransportSecurity` (HSTS/redirect/fwd-headers, non-dev); all ports→127.0.0.1 except Kong; OpenSearch secure-by-default; hello svc removed. **Global edge JWT plugin deferred → 17.6** (issuer moves in-house). Stale admin/masterdata rebuilt→Phase-14 endpoints live. |
| 16 | Audit Remediation | 16.6 Approvals sensitivity + break-glass runtime + FieldProjector + document fixes (H2/H4/H5/H9 + mediums) | ☑ | 8/8 + emr aggregation. H4 SensitiveDisclosure gate in approvals review + emr /clinical-context oversight endpoint built; H5 HttpBreakGlassProvider (fail-closed, admin /break-glass/active) in 15 svcs, sign-off corrected; H9 DocumentPolicies row-scope+audit+read/write split; H2 FieldProjector on emr clinical-context (patient identity/contact deferred — matrix gap); pharmacy SQL constant, patient PATCH audit, masterdata durable-outbox+screening audit, GateResults dedup (13 gates). ADRs 0010/0012/0014. Tests: DocumentPolicies(3)/ApprovalsSensitivity(3)/HttpBreakGlass(5)/FieldProjector(+3). Release 0/0. |
| 16 | Audit Remediation | 16.7 Live frontend wiring (scopes/roles/env/errors/i18n) (H6/H10) | ☑ | identity slice: live KC roles/users/scopes (call_center + claims_officer) + fail-closed role map (no-portal page) + provision-identity.sh. **errors DONE**: `http.ts` parses RFC 7807 problem+json → `ApiError.problem`/`.reason`; `AsyncSection` shows kind-specific headline + server `detail` (5 http.test.ts). **env DONE**: `apps/web/.env.example` documents VITE_* (LIVE/API_BASE/OIDC). **idempotency** verified already-done (HttpApiClient sends Idempotency-Key on every mutation). **i18n leak-audit DONE**: swept screens/shell/pages for hardcoded English in JSX text + aria-label/placeholder/title props — only leak was DoctorEncounter vitals labels (BP/HR/Temp/Ht/Wt), now bilingual via `t(S.*)`. **16.7 COMPLETE.** |
| 16 | Audit Remediation | 16.8 CI & test integrity (skippable tests, PG service, coverage, ESLint, OpenAPI) (H7) | ☑ | **COMPLETE.** **skippable-tests** (`7b9b665`): env-gated DB tests → `[SkippableFact]`. **backend-ci.yml** (`d431e26`): postgres:16 + warnings-as-errors build + `tools/ci/apply-migrations.sh` (hbmp_app + all 77 migrations to clean DB) + `print-test-db-env.sh` (11 integration + 13 RLS-pair + events vars → suites RUN) + `coverage-gate.sh` (domain floor `COVERAGE_MIN_DOMAIN`=55, measured 58.1%, target 80%); verified on scratch DB → 18 schemas/155 tables, full solution **0-fail 0-skip**. **ESLint** (`d6cc560`): `eslint.config.mjs` (ESLint 9 flat + typescript-eslint + react-hooks) + `lint:eslint` + frontend-ci job; workspace error-clean. **OpenAPI** (`generate-openapi.sh` + backend-ci step): Swashbuckle CLI emits every service's spec offline, fails on any generation error; verified **all 18 services** generate (audit uses the CI DB for its startup migration). Tool manifest `.config/dotnet-tools.json` |
| 16 | Audit Remediation | 16.9 Small items + ADRs 0010–0014 | ☑ | **COMPLETE.** ADRs 0010–0014 all exist. `hello` retired. CallCentre i18n enum leak fixed. **problem+json**: ~104 bare `NotFound()`→`Results.Problem` (41 files/14 svcs) + the 39 anonymous `new { error }` bodies in admin/masterdata → shared `ProblemResults` helper (Invalid/Conflict/Unprocessable; machine code in a `code` extension) — twin of GateResults (`4b63893`). **migrations-copy-to-output** on all 18 Infra projects. **idempotency** (`527926d`): Idempotency-Key on finance settlement-generate (RLS-free `finance.processed_request`, migration 0003, concurrent-key replay); the other admin/document/reporting mutations are naturally idempotent (projections dedupe on event-id, grants unique-constraint-guarded, transitions guard-checked) → no ledger needed. **eligibility consumer health-check** (`f018945`): first real `IHealthCheck` — `/health/ready` reports Healthy/Degraded on the RabbitMQ connection (thread-safe `ConsumerHealthState`). **web Dockerfile** (`e936760`): multi-stage node+pnpm→nginx SPA (build step validated; Docker daemon absent here). **FE**: CallCentre reschedule wired + history load-error distinct from empty (`f0a43a2`, +3 tests); finance export operator-date-window replaces hardcoded range (`6d90ae3`, +2 tests); RestrictedResultCard/danger text moved to real theme tokens `--surface-2`/`--st-bad-fg` — fixes a dark-mode bug (`9d9502b`). Web suite 53/53, eslint 0-error; backend suites green (admin 70, eligibility 30, finance 14). **Gate dedup**: the shared problem-shape is already in GateResults (16.6); the residual `CheckAsync` bodies genuinely vary per gate (purpose/resourceId/ProviderId/treating-set/super_admin-tenant) → a base class would be a leaky abstraction, deliberately NOT forced. **Scoped follow-up**: SPA ETag/If-Match/412 *opt-in* — the concurrency GUARD exists + is safe server-side (412 mapping + IfMatch on emr transitions, safe-by-default when absent); the SPA opt-in needs `RowVersion` surfaced on `AppointmentResponse`/`AppointmentRow`, a contract change on the **17.0 frozen-contract boundary** → do it there, not twice. |
| 17 | In-App Identity | 17.0 ADR-0015 + frozen token-contract snapshot | ☐ | |
| 17 | In-App Identity | 17.1 ASP.NET Identity store + roles/scopes as data | ☐ | |
| 17 | In-App Identity | 17.2 OpenIddict issuer (PKCE/CC/refresh, frozen claims) | ☐ | |
| 17 | In-App Identity | 17.3 In-app login pages + TOTP 2FA + recovery + step-up | ☐ | |
| 17 | In-App Identity | 17.4 In-app user/role/scope admin on the real store (closes C3) | ☐ | |
| 17 | In-App Identity | 17.5 SPA rewire to new issuer (closes H6 by design) | ☐ | |
| 17 | In-App Identity | 17.6 Cutover, Keycloak retirement, doc truth | ☐ | |
| 12 | Migration/Go-live | 12.1 Migration pipelines · 12.2 Release mgmt · 12.3 Pilot + hypercare | ☐ | |
| 13 | Interoperability | 13.1 FHIR R4 façade · 13.2 Adapters/ACL · 13.3 Interop test harness | ☐ | |

> **Phase 17 (In-App Identity)** implements ADR-0015 (Keycloak → ASP.NET Identity + OpenIddict). Sequence:
> 16.1–16.2 → **17.1–17.6** → remaining 16.x — phase 16's Keycloak-specific steps (16.3 MFA enforcement,
> 16.7 realm/scope reconciliation) land on the NEW issuer instead; do not fix the realm twice.

> **Phase 16 (Audit Remediation)** comes from `docs/AUDIT-2026-07-26.md` and **gates 12 (go-live)**. Note:
> 11.1–11.3 were ticked after the audit snapshot; the audit's 11.x observations may be partially stale, but
> findings C1–C3 and H1–H10 were verified against code and stand until closed.
>
> **Phase 16 status: COMPLETE for all work actionable now** — 16.1, 16.2, 16.4, 16.6, 16.7, 16.8, 16.9 done;
> 16.3 (admin MFA, C3) and 16.5's global edge-JWT plugin (H3) are **deliberately folded into Phase 17** because
> the issuer moves in-house (ADR-0015) and doing them on Keycloak first would be thrown-away work. Everything that
> can be closed without the identity migration is closed. **Next: Phase 17.0.**

## Environment notes
- .NET 8 SDK: user-local `~/.dotnet` (use `./dotnet.sh`). Node 20, psql 17 present.
- **Frontend (Phase 9):** pnpm workspace at repo root (`pnpm-workspace.yaml`); `apps/design-system` (9.1),
  `apps/web` (9.2 portals + 9.3 flagship screens), and `libs/contracts` (`@mersal/contracts` — shared zod
  mirror) are live. Node 20 ⇒ use **pnpm 9** (`npx pnpm@9.15.9 …`); pnpm ≥10 needs Node 22. Filters:
  `pnpm --filter @mersal/{design-system,web,contracts} {dev,test,build,lint}` (design-system + web test =
  vitest unit + **axe** gate). Frontend suite: contracts 5 + design-system 18 + web 18 = **41 tests**. The
  six flagship screens are `React.lazy` (per-portal chunks); the dev app uses `DevApiClient` fixtures
  (bilingual, contract-valid, no PHI) — swap `HttpApiClient` once services are reachable behind Kong. CI in
  `.github/workflows/frontend-ci.yml`.
- **Phase 10 (Case + Finance):** two new .NET services — `case-service` (schema `case`, the `case-assignment`
  ABAC condition in `libs/authz`, beneficiary-360 coordination summary, PHI-read audited) and `finance-service`
  (schema `finance`, the `FinanceProjection` whitelist + `FinanceCannotReadDiagnosisTests` proving finance ≠
  diagnosis, settlements priced from `provider_contract`). Plus the **Case Manager + Finance portals** in
  `apps/web` (10.3): `CaseManager.tsx` (My Cases → coordination-360 with masked clinical sections + tasks;
  Escalations) and `FinancePortal.tsx` (Utilization / Settlements / Summaries with US-073 data-table toggle /
  audited Exports) — both `React.lazy` chunks. New `@mersal/contracts` modules `case.ts` + `finance.ts` (the
  finance≠diagnosis + coordination-summary invariants are structural + contract-tested). Frontend suite:
  contracts 9 + design-system 18 + web 21 = **48 tests**. Backend: case-service 27 + finance-service 14.
  DB-integration tests env-gated (`CASE_TEST_DB` / `FINANCE_TEST_DB`, hbmp superuser conn).
- **Phase 14 (Branch & Sensitivity)** is listed above **before 7** because it is a cross-cutting *retrofit* of
  `libs/authz`, identity, provider, emr and orders — that is its correct execution slot. Phases 7–10 were built
  ahead of it, so landing 14 also means revisiting the approvals worklist (member-scoped, no sensitive-result
  content) and the phase-9 portals (branch switcher, locked-result state). Migrations are additive only and the
  full existing suite must stay green. Design: `HBMP-Design/37-branch-scoping-and-clinical-sensitivity.md`;
  prompt: `HBMP-Design/claude-code-prompts/phase-14-branch-scoping-and-sensitivity.md`.
- **Phase 11 (Hardening & NFR assurance)** is cross-cutting evidence, not new features. **11.1** `/perf` k6 suite
  (eligibility/consume-race/worklists/dashboards/mixed-soak, thresholds = NFR §1/§2 targets → CI-gated) + deterministic
  synthetic volume generator (NFR-012, no PHI, verified runnable) + `docs/PERFORMANCE-BASELINE.md` (measured cols
  = PENDING-staging, not fabricated) + ADR 0009 (RLS-first indexes + Valkey invalidation) + `perf-ci.yml`. **11.2**
  `docs/security/` STRIDE model + OWASP API Top 10 sweep; `security-ci.yml` (gitleaks + Trivy SCA/config/image +
  CodeQL + ZAP, block on Critical/High); `docs/compliance/security-sign-off.md` (✅ code/CI/tests vs 🟡
  operational-gate-on-target-infra). **11.3** Prometheus `/metrics` now live on **all 17 services** (OTel Prometheus
  exporter + runtime instrumentation, solution 0-warning) + `services` scrape job + Alertmanager + Grafana
  dashboards-as-code (golden-signals + business-kpis) + alert rules (SLO burn / event-bus / failed-consume /
  approvals-SLA / auth-anomaly / audit-chain) + 7 runbooks + `infra/dr/restore-rehearsal.sh` **executed** (138
  tables/18 schemas reconciled exactly, audit chain-linkage intact → PASS; second-site failover pending target k3s).
  The ✅ items gate now; the 🟡 items need staging/k3s/OpenBao/external-pentest to sign off. Prompt:
  `HBMP-Design/claude-code-prompts/phase-11-hardening-and-nfr.md`.
- Docker/Compose, Helm, OpenTofu: **not yet installed** (Docker needs root). Tier 1 infra authored in `infra/compose`; run once Docker is installed.
- Repo initialized in place at `/home/mohab/Mersal` with `HBMP-Design/` as a subfolder.
