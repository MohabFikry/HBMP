# Phase 10 — Case Management & Finance (R5)

**Goal:** Build the `case-service` (care/benefit coordination over an assigned case load) and the `finance-service` (utilization, provider settlements, financial summaries, exports) — each strictly minimum-necessary. Case Managers get a **beneficiary-360 scoped to their assigned cases**; Finance produces cost and settlement read-models **with zero clinical-diagnosis exposure**, enforced by field-level projection and proven by an authorization test.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

---

## Skills to activate
> Activate `case-management-system`, `healthcare-reporting-kpis`, `medical-claims-engine` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

Open these before coding:

- [../10-role-matrix.md](../10-role-matrix.md) — §3.11 **Case Managers** (`beneficiary:assigned`, T3 summary-level clinical, access follows assignment/unassignment revokes) and §3.12 **Finance** (cost-line only, no clinical). Role capability matrix rows for both.
- [../11-permission-matrix.md](../11-permission-matrix.md) — §3.2 clinical zone: Case Managers `R🔒(summary)🟠ASG`, **Finance = all ❌ on the clinical row**; §4 field-level: Case Managers `diagnosis visible(coord)🟠ASG`, **Finance `diagnosis` = denied** (whole clinical row denied), `financials` visible, `pii` masked(min). The **hard-rule check**: "Finance `diagnosis` = ❌" must always hold.
- [../03-user-personas.md](../03-user-personas.md) — **A8 Nadia (Case Manager)**, **A9 Tarek (Finance Officer)**.
- [../14-navigation-structure.md](../14-navigation-structure.md) — §2.7 Case Manager portal (My Cases, Beneficiary 360, Escalations, Coordination Tasks); §2.8 Finance portal (Utilization, Provider Settlements, Financial Summaries, Exports) — "Nav exposes no diagnosis/clinical routes."
- [../12-ui-wireframes.md](../12-ui-wireframes.md), [../0B-DESIGN-SYSTEM-UI.md](../0B-DESIGN-SYSTEM-UI.md) — portal layouts + shared design system.
- [../07-functional-requirements.md](../07-functional-requirements.md) — FR-ELG-007 (Case Manager manual eligibility override with reason+audit); finance/utilization reporting FRs.
- [../19-audit-strategy.md](../19-audit-strategy.md) — immutable hash-chained audit; `data.export` is a distinct high-severity event.
- [../22-data-dictionary.md](../22-data-dictionary.md) §5.3 — `provider_contract` / `contract_service_line` (agreed prices, owned by provider-service).
- [../32-user-stories.md](../32-user-stories.md) — case-management and finance stories; US-095 "Finance sees no diagnoses" navigation rule.

Depends on phases 1 (beneficiary/policy), 4 (EMR/orders), 5/6 (fulfillment/dispense), 7 (authorizations), 8 (reporting read-models), and 0 (identity, ABAC, audit spine). Reuses the provider-network service's `provider_contract` prices — never copy them; read them.

---

## Prompts

### 10.1 — `case-service`: cases, assignment, beneficiary-360, tasks, escalations + ABAC `case-assignment`

```text
Build the case-service (.NET 8, bounded context `case`, schema `case`) for care/benefit coordination over an assigned case load. Read ../10 §3.11, ../11 §3.2 + §4 (Case Manager rows), ../03 A8, ../14 §2.7, and the case-management user stories in ../32 first.

DOMAIN & DATA (schema `case`, soft-delete + history on all)
- `case` — id uuid v7, business key `CASE-YYYY-XXXX`, beneficiary_id, category (complex|chronic|vulnerable|escalation), status (Open|Active|OnHold|Resolved|Closed), priority, opened_by, opened_at, summary, created/updated audit columns.
- `case_assignment` — id, case_id, case_manager_id (identity user), assigned_at, unassigned_at (nullable), active bool. Assignment is the ABAC anchor: an active row grants access; setting unassigned_at/active=false REVOKES it (../10 §3.11 "unassignment revokes it").
- `coordination_task` — id, case_id, title, description, assignee_id, due_at, status (Todo|InProgress|Done|Cancelled), outcome_note.
- `escalation` — id, case_id, raised_by, raised_to_role (e.g., Medical Approval/Director), reason, status (Raised|Acknowledged|Resolved), timestamps.

ABAC ATTRIBUTE
- Register a `case-assignment` ABAC attribute resolved from active `case_assignment` rows: a Case Manager may act on a case (and reach that beneficiary's data) ONLY when they hold an active assignment. Enforce at the policy layer, not just in controllers. Deny (403, audited) otherwise.

BENEFICIARY-360 (scoped, minimum-necessary, ASG)
- GET /cases/{id}/beneficiary-360 assembles a COORDINATION view by calling sibling services with the caller's purpose (coordination) and ABAC `case-assignment`:
  * eligibility + coverage summary (remaining limits) from eligibility/policy;
  * care-plan + open approvals (status) from approvals;
  * upcoming/past appointments;
  * a CLINICAL SUMMARY projection only — per ../11 §4, Case Managers get `diagnosis visible(coord)` but `emr_note/prescription/lab_result/imaging_result` are MASKED at summary level unless the care plan requires detail. Return an explicit field-scoped DTO, never raw EMR records.
- Every 360 assembly writes a PHI-read audit event (actor, case_id, beneficiary_id, fields returned) via the shared audit client.

APIs (/api/v1): CRUD for case/assignment/task/escalation; GET /cases (My Cases = caller's assigned, cursor paged); POST /cases/{id}/escalate; POST /cases/{id}/assign + /unassign (revocation path). Manual eligibility override (FR-ELG-007) is initiated here with mandatory reason + audit, delegated to eligibility-service.

ACCEPTANCE
- Given a Case Manager with an active assignment to CASE-X, When they open /beneficiary-360, Then they see eligibility/coverage/care-plan/appointments + a coordination clinical SUMMARY, and a PHI-read audit event is written.
- Given a Case Manager WITHOUT an assignment (or after unassignment), When they call any case/360 endpoint for that case, Then 403 (audited) and no beneficiary data leaks.
- Given an escalation raised to Medical Approval, When created, Then it is trackable and audited.

Ship: EF migrations, OpenAPI 3.1, unit + integration + ABAC authorization tests (assigned vs. unassigned), README/ADR. Publish `CaseOpened/CaseAssigned/CaseEscalated/TaskCompleted` via the outbox.
```

### 10.2 — `finance-service`: utilization & cost read-models, settlements, summaries, exports — no clinical

```text
Build the finance-service (.NET 8, bounded context `finance`, schema `finance`) producing cost/utilization read-models, provider settlements, financial summaries, and audited exports — with a HARD invariant: Finance can NEVER read diagnoses or any clinical detail. Read ../11 §3.2 (Finance clinical row = all ❌) + §4 (Finance `diagnosis`=denied, whole clinical row denied, `financials` visible, `pii` masked-min), ../10 §3.12, ../03 A9, ../14 §2.8, ../22 §5.3, ../19 first.

READ-MODELS (built from domain events via the outbox/subscriptions; NEVER by joining clinical tables)
- `utilization_fact` — beneficiary_id, coverage_category, service_code (CPT/LOINC/ATC BILLING code only), provider_id, authorized_qty, delivered_qty, unit_cost, line_cost, occurred_at. Populated from order-fulfillment, dispense, and authorization events — carry ONLY billing codes and amounts, NEVER `diagnosis`/`emr_note`/`lab_result`/`imaging_result`. The event contracts consumed here must expose no clinical fields; if a source event carries clinical data, project it away at the subscription boundary.
- `settlement` / `settlement_line` — provider_id, contract_id, period, per-line service_code + delivered_qty + agreed_unit_price + line_total, status (Draft|Submitted|Approved|Paid). Prices come from provider-service `provider_contract`/`contract_service_line` (../22 §5.3) — READ them via the provider API/read-model; do not duplicate or mutate contract prices here.

FIELD-LEVEL PROJECTION (the core control)
- Introduce a `FinanceProjection` layer that whitelists exactly the allowed fields (billing service_code, quantities, amounts, masked-min PII, coverage category, provider) and physically cannot surface clinical fields. All finance DTOs derive from it. Add a compile-time/analyzer or unit guard asserting no clinical field name appears in any finance DTO.

APIs (/api/v1)
- GET /finance/utilization — filter by period/category/provider/beneficiary; aggregates (authorized vs. delivered, spend). No clinical filter or column exists.
- POST /finance/settlements — generate a settlement for a provider+period from utilization_fact × contract price; GET/list; POST /{id}/submit|approve (SoD-gated per ../11 release rule).
- GET /finance/summaries — spend/utilization roll-ups for donor/leadership reporting.
- POST /finance/exports — CSV/XLSX export of utilization/settlement/summary. Export is a DISTINCT elevated action: masked PII, audited as high-severity `data.export` (../19) with actor, filter, row count, correlation id.

AUTHORIZATION TEST (must exist and pass — proves the invariant)
- Write an authorization/integration test `Finance_Cannot_Read_Diagnosis`: seed a beneficiary with a diagnosis; assert (a) no finance endpoint returns any clinical/diagnosis field; (b) a finance principal calling any EMR/diagnosis endpoint gets 403 (audited); (c) the FinanceProjection guard rejects a clinical field at build/unit time. Reference ../11 hard-rule "Finance `diagnosis` = ❌".

ACCEPTANCE
- Given delivered services, When Finance opens /utilization, Then they see authorized-vs-delivered quantities and spend by billing code — and no diagnosis/clinical field anywhere.
- Given a provider + period, When Finance generates a settlement, Then lines are priced from provider_contract agreed prices and totals are correct.
- Given any export, When it runs, Then PII is masked and a high-severity `data.export` audit event is written.
- Given a finance principal, When they attempt to read a diagnosis, Then 403 and the authz test proves it.

Ship: EF migrations, OpenAPI 3.1, unit + integration + the authorization test, README/ADR.
```

### 10.3 — Finance portal + Case Manager portal (frontend)

```text
Build the Case Manager and Finance portals in the React+TS app using the shared design system. Read ../14 §2.7 + §2.8, ../12 (wireframes), ../0B (design system), and ../21 (accessibility) first. Both portals are minimum-necessary, WCAG 2.2 AA, bilingual (AR RTL / EN LTR).

CASE MANAGER PORTAL (nav per ../14 §2.7)
- Routes: My Cases (assigned case load list, priority/status filters), Beneficiary 360 (eligibility/coverage summary, care plan, open approvals, appointments, coordination clinical SUMMARY), Escalations, Coordination Tasks (kanban Todo/InProgress/Done).
- 360 renders ONLY the field-scoped DTO from 10.1; masked fields show a "summary only" affordance, never raw clinical records. Attempting a case not assigned shows an authorized empty/deny state (mirrors the 403).

FINANCE PORTAL (nav per ../14 §2.8)
- Routes: Utilization (authorized-vs-delivered, spend by category/provider), Provider Settlements (generate/review/approve, line detail with agreed prices), Financial Summaries (donor/leadership roll-ups), Exports (masked, download).
- The nav MUST expose NO diagnosis/clinical route (../14: "Nav exposes no diagnosis/clinical routes"). Route guards + the API contract make clinical data unreachable from this portal.

CROSS-CUTTING
- Reuse the shared component library, tokens, RTL mirroring, and focus/keyboard patterns from ../0B; run the a11y checks from ../21 in CI. Every export click confirms and is audited server-side.

ACCEPTANCE
- Given a Case Manager, When they open an assigned case's 360, Then coordination data renders with clinical shown at summary level only, in AR RTL and EN, keyboard + screen-reader accessible.
- Given a Finance user, When they navigate the portal, Then there is no route or control that reaches a diagnosis or clinical note.
- Given an export, When triggered, Then the user confirms and a server-side audit event is recorded.

Ship: components, route guards, i18n (AR/EN) strings, unit + component + a11y (axe) tests, Storybook entries, and Playwright E2E for the two happy paths + one deny path.
```

---

## Guardrails

- **Finance ≠ diagnosis — enforced and TESTED.** The clinical row is denied to Finance at row and field level (../11 §3.2/§4). The `FinanceProjection` whitelist makes clinical fields structurally unreachable; the `Finance_Cannot_Read_Diagnosis` authz test is a required, green gate. Any change that could surface a clinical field to Finance is rejected at review.
- **Case access is scoped to assignment.** The `case-assignment` ABAC attribute governs every case/360 endpoint; unassignment revokes access immediately. No cross-case-load reads.
- **Minimum-necessary everywhere.** Case Manager 360 is a coordination summary (diagnosis for coordination, other clinical masked); Finance sees billing codes + amounts + masked-min PII only.
- **Exports are audited + masked.** Every export is a distinct high-severity `data.export` event (../19) with masked PII; no bulk PHI export.
- **Contract prices are read, not owned.** Settlements price from provider-service `provider_contract` — never copy or mutate contract data in finance.
- **Immutable audit + soft-delete/history** on every read of PHI, case action, settlement, and export.

## Done when

- Case Managers work their **assigned** cases with a beneficiary-360 (eligibility, care plan, approvals, appointments, coordination clinical summary), open/track coordination tasks, and raise escalations — all assignment-scoped and PHI-read audited; unassignment revokes access.
- Finance produces utilization (authorized vs. delivered), provider settlements priced from `provider_contract`, financial summaries, and masked audited exports — with **no clinical or diagnosis exposure anywhere**, proven by the passing `Finance_Cannot_Read_Diagnosis` test.
- Both portals ship bilingual (AR RTL/EN) and WCAG 2.2 AA; the Finance nav exposes no clinical route.
- All acceptance criteria pass; unit/integration/authz/a11y/E2E tests green; OpenAPI + README/ADR updated. Global Definition of Done met.
