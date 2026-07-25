# Mersal HBMP — Build Status & Execution Roadmap

Phases run in dependency order; **one sub-prompt ≈ one reviewable PR** (see `HBMP-Design/claude-code-prompts/00-MASTER-PROMPT-LIST.md`). Status: ☐ not started · ◐ in progress · ☑ done.

## Dependency order
`0 → 0b → 1 → 2 → 2b → 3 → 4 → 5,6 (parallel) → 7 → 8 → 10 → 10b`; `8b` + `9` run continuously from R0/R1; `11` gates go-live; `12` after 11; `13` after core. `10b` (claims) needs 5/6 fulfillment records, 2b contracts/tariffs, and 7 authorizations.

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
| 10b | Claims Mgmt | 10b.1 `claims-service` + auto-derived claims (no double-billing) | ☐ | |
| 10b | Claims Mgmt | 10b.2 Batching + batch lifecycle (single-open-batch) | ☐ | |
| 10b | Claims Mgmt | 10b.3 Automated pre-adjudication (9-step, all reasons) | ☐ | |
| 10b | Claims Mgmt | 10b.4 Officer review + line-level decisions (SoD, dual control) | ☐ | |
| 10b | Claims Mgmt | 10b.5 Provider-submitted claims + document matching | ☐ | |
| 10b | Claims Mgmt | 10b.6 Beneficiary reimbursement + OCR (assistive) | ☐ | |
| 10b | Claims Mgmt | 10b.7 Reconciliation + append-only adjustments | ☐ | |
| 10b | Claims Mgmt | 10b.8 Settlement advice + exports (no payment execution) | ☐ | |
| 10b | Claims Mgmt | 10b.9 Appeals + claims KPIs | ☐ | |
| 11 | Hardening/NFR | 11.1 Perf/scale · 11.2 Security sign-off · 11.3 DR/observability | ☐ | |
| 12 | Migration/Go-live | 12.1 Migration pipelines · 12.2 Release mgmt · 12.3 Pilot + hypercare | ☐ | |
| 13 | Interoperability | 13.1 FHIR R4 façade · 13.2 Adapters/ACL · 13.3 Interop test harness | ☐ | |

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
- Docker/Compose, Helm, OpenTofu: **not yet installed** (Docker needs root). Tier 1 infra authored in `infra/compose`; run once Docker is installed.
- Repo initialized in place at `/home/mohab/Mersal` with `HBMP-Design/` as a subfolder.
