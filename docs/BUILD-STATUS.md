# Mersal HBMP — Build Status & Execution Roadmap

Phases run in dependency order; **one sub-prompt ≈ one reviewable PR** (see `HBMP-Design/claude-code-prompts/00-MASTER-PROMPT-LIST.md`). Status: ☐ not started · ◐ in progress · ☑ done.

## Dependency order
`0 → 0b → 1 → 2 → 2b → 3 → 4 → 5,6 (parallel) → 7 → 8 → 10`; `8b` + `9` run continuously from R0/R1; `11` gates go-live; `12` after 11; `13` after core.

## Progress

| # | Phase | Sub-prompt | Status | PR/commit |
|---|-------|-----------|--------|-----------|
| 0 | Foundations | 0.1 Monorepo scaffold, CI/CD, dev IaC | ☑ | cac74b3 |
| 0 | Foundations | 0.2 Identity & access (Keycloak, MFA) + `libs/auth` | ☐ | |
| 0 | Foundations | 0.3 Audit spine: `audit-service` + `libs/audit-client` | ☐ | |
| 0 | Foundations | 0.4 AuthZ engine: `libs/authz` (RBAC+ABAC, row+field) | ☐ | |
| 0 | Foundations | 0.5 Service template + Kong gateway + `libs/events` + observability | ☐ | |
| 0b | Master Data | 0b.1 `masterdata-service` schema + read/search APIs | ☐ | |
| 0b | Master Data | 0b.2 Loaders: ingest real ICD-10/CPT/ATC drugs | ☐ | |
| 0b | Master Data | 0b.3 Interactions/allergens seed + validation endpoints | ☐ | |
| 1 | Registration | 1.1 `patient-service` | ☐ | |
| 1 | Registration | 1.2 `policy-service` | ☐ | |
| 1 | Registration | 1.3 `document-service` integration | ☐ | |
| 1 | Registration | 1.4 Registration workflow + activation | ☐ | |
| 2 | Eligibility | 2.1 `eligibility-service` + cache | ☐ | |
| 2 | Eligibility | 2.2 Reception search (min-necessary) | ☐ | |
| 2 | Eligibility | 2.3 Visit gating + encounter stub | ☐ | |
| 2b | Provider Network | 2b.1 `provider-service` | ☐ | |
| 2b | Provider Network | 2b.2 Onboarding workflow | ☐ | |
| 2b | Provider Network | 2b.3 Provider isolation (ABAC PO + RLS) | ☐ | |
| 3 | Appointments | 3.1 Appointment domain + slot booking | ☐ | |
| 3 | Appointments | 3.2 Reschedule/cancel/no-show | ☐ | |
| 3 | Appointments | 3.3 Queue + reminders hook | ☐ | |
| 4 | Clinical EMR | 4.1 `emr-service` + treating-relationship ABAC | ☐ | |
| 4 | Clinical EMR | 4.2 `orders-service` + approval routing | ☐ | |
| 4 | Clinical EMR | 4.3 `pharmacy-service` Rx + referral creation | ☐ | |
| 5 | Lab/Imaging | 5.1 Provider order queue + search | ☐ | |
| 5 | Lab/Imaging | 5.2 Atomic idempotent consume | ☐ | |
| 5 | Lab/Imaging | 5.3 Result upload + routing | ☐ | |
| 6 | Pharmacy | 6.1 Dispensable search | ☐ | |
| 6 | Pharmacy | 6.2 Partial dispensing (batch/expiry) | ☐ | |
| 6 | Pharmacy | 6.3 Substitution + out-of-stock | ☐ | |
| 7 | Approvals | 7.1 `approvals-service` + worklist + review | ☐ | |
| 7 | Approvals | 7.2 Decisions + downstream effects | ☐ | |
| 7 | Approvals | 7.3 Break-glass + SLA/TAT | ☐ | |
| 8 | Notify+Reporting | 8.1 `notification-service` | ☐ | |
| 8 | Notify+Reporting | 8.2 `reporting-service` KPI read-models | ☐ | |
| 8 | Notify+Reporting | 8.3 Executive dashboard contracts | ☐ | |
| 8b | Admin Platform | 8b.1 User/role admin + SoD + access review | ☐ | |
| 8b | Admin Platform | 8b.2 Master-data/template/config admin | ☐ | |
| 8b | Admin Platform | 8b.3 Tenant/provider + break-glass governance | ☐ | |
| 9 | Frontend | 9.1 Design system in code | ☐ | |
| 9 | Frontend | 9.2 Role portals + permission routing | ☐ | |
| 9 | Frontend | 9.3 Flagship screens | ☐ | |
| 10 | Case + Finance | 10.1 `case-service` + beneficiary-360 | ☐ | |
| 10 | Case + Finance | 10.2 `finance-service` (no-diagnosis) | ☐ | |
| 10 | Case + Finance | 10.3 Case + Finance portals | ☐ | |
| 11 | Hardening/NFR | 11.1 Perf/scale · 11.2 Security sign-off · 11.3 DR/observability | ☐ | |
| 12 | Migration/Go-live | 12.1 Migration pipelines · 12.2 Release mgmt · 12.3 Pilot + hypercare | ☐ | |
| 13 | Interoperability | 13.1 FHIR R4 façade · 13.2 Adapters/ACL · 13.3 Interop test harness | ☐ | |

## Environment notes
- .NET 8 SDK: user-local `~/.dotnet` (use `./dotnet.sh`). Node 20, psql 17 present.
- Docker/Compose, Helm, OpenTofu: **not yet installed** (Docker needs root). Tier 1 infra authored in `infra/compose`; run once Docker is installed.
- Repo initialized in place at `/home/mohab/Mersal` with `HBMP-Design/` as a subfolder.
