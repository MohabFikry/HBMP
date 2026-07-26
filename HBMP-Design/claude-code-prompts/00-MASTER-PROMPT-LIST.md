# Mersal HBMP — Claude Code Prompt Library (Master List)

This folder turns the **HBMP design set** (`../HBMP-Design/*.md`) into a sequence of ready-to-run **Claude Code** prompts that build the platform phase by phase. Each phase file contains one or more self-contained prompts you paste into a Claude Code session. They reference the design documents and your **Master Lists** reference data.

> **Golden rule:** do not skip the design docs. Every prompt says *which* design files to read first. Claude Code should open those, then implement. Build vertically (a thin end-to-end slice) before widening.

---

## How to use this library

1. **Seed the repo context.** Copy the content of [`00-CLAUDE-MD-AND-CONVENTIONS.md`](00-CLAUDE-MD-AND-CONVENTIONS.md) into a `CLAUDE.md` at the root of your new repository. Claude Code auto-loads it every session, so the stack, conventions, and Definition of Done apply to every prompt without repeating them.
2. **Make the design docs reachable.** Keep the `HBMP-Design/` folder (or a copy) inside or beside the repo so Claude Code can read the referenced files. Same for the `Master Lists/` folder (ICD-10, CPT, ATC drugs).
3. **Run phases in order.** Each phase = a focused session (or a few). Start the session by pasting the phase's **Prompt**. Let Claude Code plan, implement, and write tests. Review the diff, run tests, and only then continue to the next prompt.
4. **Enforce the gates.** Nothing is "done" until it meets the Definition of Done in the conventions file (tests green, security + accessibility + audit checks, min-necessary data rules). Security, a11y, and audit are per-prompt acceptance criteria, not a later phase.
5. **Commit per prompt.** One prompt ≈ one reviewable PR. Use the commit convention in the conventions file.

---

## Phase map

| Phase | Prompt file | Goal | Read these design docs first | Master-list inputs | Release |
|-------|-------------|------|------------------------------|--------------------|---------|
| 0 | [phase-0-foundations.md](phase-0-foundations.md) | Monorepo, IaC, CI/CD, API gateway, identity+MFA, **audit spine**, RBAC/ABAC engine, observability | 0A, 16, 18, 19, 25 | — | R0 |
| 0b | [phase-0b-master-data.md](phase-0b-master-data.md) | Master-data service + **ingest ICD-10, CPT, ATC drugs, drug interactions, allergens** from Master Lists | 0A §3, 15, 22, 16 | **`Master Lists/`** + `Raw Files/` | R0 |
| 1 | [phase-1-registration-policy.md](phase-1-registration-policy.md) | Beneficiary + identifiers, documents, policy/coverage, registration→approval→activation | 01, 04, 07, 15, 22, 23, 32 | — | R1 |
| 2 | [phase-2-eligibility-reception.md](phase-2-eligibility-reception.md) | Eligibility service + reception search + min-necessary result card + visit gating | 07, 11, 13, 17, 23, 32 | — | R1 |
| 3 | [phase-3-appointments.md](phase-3-appointments.md) | Appointments: walk-in/scheduled/referral/follow-up, reschedule, no-show, queue | 05, 07, 13, 23, 32 | — | R2 |
| 4 | [phase-4-clinical-emr-orders.md](phase-4-clinical-emr-orders.md) | Encounter/EMR (SOAP, vitals, allergies, dx), **investigation orders + e-prescriptions** | 07, 15, 22, 23, 24, 32 | ICD-10, CPT, Drug (from 0b) | R2 |
| 5 | [phase-5-lab-imaging.md](phase-5-lab-imaging.md) | Provider fulfillment: order queue, **atomic idempotent consume**, result upload, partial | 07, 22, 23, 24, 26, 32 | LOINC/CPT | R3 |
| 6 | [phase-6-pharmacy.md](phase-6-pharmacy.md) | Pharmacy: search, **partial dispensing**, batch/expiry, substitution, out-of-stock | 07, 22, 23, 24, 32 | Drug/ATC | R3 |
| 7 | [phase-7-approvals.md](phase-7-approvals.md) | Medical approval worklist, decisions (partial/override/emergency/manual), rationale+audit | 07, 11, 13, 19, 23, 24, 32 | — | R4 |
| 8 | [phase-8-notifications-reporting.md](phase-8-notifications-reporting.md) | Notification engine + reporting/dashboards (KPIs, TAT, utilization) | 07, 08, 32 | — | R5 |
| 2b | [phase-2b-provider-network.md](phase-2b-provider-network.md) | Provider network + onboarding + **provider isolation** (needed before lab/imaging/pharmacy) | 07, 10, 15, 18, 22 | — | R1→R3 |
| 9 | [phase-9-frontend-portals.md](phase-9-frontend-portals.md) | Design system in code + role portals (RTL/a11y) matching the prototypes | 0B, 09, 12, 13, 14, 21, prototypes | — | R1–R5 (per portal) |
| 8b | [phase-8b-admin-platform.md](phase-8b-admin-platform.md) | Admin: users/roles, master-data admin, config, tenant, break-glass, access reviews | 10, 11, 18, 19 | — | R0→R5 |
| 10 | [phase-10-case-mgmt-finance.md](phase-10-case-mgmt-finance.md) | Case management (beneficiary-360) + Finance (utilization/settlements/exports; **finance≠diagnosis**) | 07, 10, 11, 22 | — | R5 |
| 10b | [phase-10b-claims-management.md](phase-10b-claims-management.md) | Claims capture (auto-derived/provider-submitted/**reimbursement+OCR**), batching, pre-adjudication, **line-level officer decisions**, reconciliation + adjustments, settlement advice (**no payment execution**) | **36**, 22, 23, 11, 07, 16 | — | R6/post-v1 |
| 11 | [phase-11-hardening-and-nfr.md](phase-11-hardening-and-nfr.md) | Performance/load, security hardening + pen-test, HA/**DR**, observability/SLOs, runbooks | 08, 18, 25, 26, 27, 34 | — | pre-prod gate |
| 12 | [phase-12-migration-and-golive.md](phase-12-migration-and-golive.md) | Data migration (providers/beneficiaries), gated release, pilot go-live + hypercare | 20, 25, 29, 35 | Master Lists (via 0b) | go-live |
| 13 | [phase-13-interoperability-and-roadmap.md](phase-13-interoperability-and-roadmap.md) | FHIR R4 facade + integration adapters (UNHCR/gov/HL7) + OCR/NLP hooks | 16, 17, 20, 35 | — | R5+/roadmap |
| 14 | [phase-14-branch-scoping-and-sensitivity.md](phase-14-branch-scoping-and-sensitivity.md) | **Cross-cutting retrofit:** six Mersal **branches** + active-branch switcher and server-side **branch scoping** (operational roles branch-scoped, approvals/managers member-scoped, external providers provider-scoped), **practitioner records + structured specialty**, **examination types with sensitivity** and **default-deny sensitive results** released only via a justified, time-boxed, fully audited grant | **37**, 22, 23, 10, 11, 07, 16, 14 | — | retrofit / R4-prep |
| 15 | [phase-15-call-centre-portal.md](phase-15-call-centre-portal.md) | **End-to-end Call Centre agent portal:** new `callcentre-service` (`call_interaction` + `caller_verification`), **verify the caller before any disclosure** (≥2 identifier **types**, values never stored), minimum-necessary member 360 (eligibility + coverage/remaining limits, editable contacts, appointments **across all six branches**, open referrals + follow-ups due, **nothing clinical**), book/reschedule/cancel through the **existing** emr appointment engine (mandatory cancel reason), contact updates, call logging, KPIs + notifications — everything audited and correlated by `call_ref` | **37 §3**, 10, 11, 23, 13, 12, 14, 0B, 21 | — | R5/portal |

| 16 | [phase-16-audit-remediation.md](phase-16-audit-remediation.md) | **Audit remediation (2026-07-26):** engage the built defenses — durable transactional outbox, admin authz middleware+MFA, RLS as `hbmp_app` everywhere, Kong JWT + missing routes, approvals **sensitivity retrofit**, break-glass runtime provider, FieldProjector adoption, live frontend wiring (scopes/roles/env), CI & test integrity, doc truth | **`docs/AUDIT-2026-07-26.md`**, 37, 18, 11 | — | pre-11.3 gate |

| 18 | [phase-18-e2e-remediation.md](phase-18-e2e-remediation.md) | **E2E review remediation (Audit R2):** Gate A benefit/money **correctness** (coverage limits never bind, rollups erase adjustments, cap inversion, lost update) · Gate B security closure (secrets regression, RLS deny-all trap, admin/issuer authz, CSRF, scope fail-open) · Gate C last-mile wiring (5-min sessions, branch header, interop routing, report-access UI, SPA) · Gate D UX safety (silent write failures, false-eligible chip, mobile nav) · Gate E CI truth · Gate F enhancements | **`docs/AUDIT-R2-E2E.md`**, 23, 36, 37, 11, 18, 0B, 21 | — | **gates pilot & go-live** |

> **This is a full production build set — all functionality, not just design.** Beyond the core journey it covers the provider network, every admin/platform capability, case management, finance, non-functional hardening (performance, security, DR, observability), data migration, gated go-live, and interoperability. Together the phases realize every role/portal and service in the architecture ([../HBMP-Design/16-service-architecture.md](../HBMP-Design/16-service-architecture.md)).

Release definitions and exit criteria: [../HBMP-Design/29-delivery-plan.md](../HBMP-Design/29-delivery-plan.md). Sprint mapping: [../HBMP-Design/33-sprint-roadmap.md](../HBMP-Design/33-sprint-roadmap.md).

---

## Dependency order

```
0 → 0b → 1 → 2 → 2b → 3 → 4 → 5,6 (parallel) → 14 (retrofit) → 7 → 8 → 10 → 10b
8b (admin) + 9 (frontend) run continuously alongside from R0/R1 — 9 after 14.
15 (call centre portal) after 3 + 2 + 1 + 8 + 9 — additive, pairs with 14.
11 (hardening/NFR) is cross-cutting and gates go-live.
12 (migration & go-live) after the functional set + 11.
13 (interoperability) after core services; DPIA gate before any external integration.
```

- Phase 0 must land first (identity, audit, ABAC, gateway are used by everything).
- 0b (master data) is required before 4 (orders/prescriptions reference ICD/CPT/Drug).
- **2b (provider network) must precede 5 and 6** — labs, imaging centers, and pharmacies must be onboarded (with isolation) before they can fulfill orders/prescriptions.
- 5 and 6 can be built in parallel after 4 and 2b.
- **14 (branch scoping & clinical sensitivity) is a cross-cutting RETROFIT of the already-built services — it runs next, before 7 and before 9.** It touches `libs/authz`, identity, provider, emr, and orders, so it must land while those surfaces are still the only consumers: **7 (approvals) has to be built already knowing it is member-scoped across all branches and that sensitive results are content-restricted from the approval team** (retrofitting that into a finished worklist means reworking its queries, projections, and authorization tests), and **9 (frontend) needs the branch switcher, the active-branch context, and the locked/restricted-result state** as first-class parts of the design system rather than bolt-ons. Every migration is additive and every existing test must stay green. Authoritative design: [../HBMP-Design/37-branch-scoping-and-clinical-sensitivity.md](../HBMP-Design/37-branch-scoping-and-clinical-sensitivity.md).
- 7 depends on 4 (it authorizes orders/prescriptions) and 19 (audit), and on **14** for its member scope + sensitive-result restrictions.
- 8b (admin/platform) is incremental: user/role admin lands early (R0/R1), master-data & config admin follow their services.
- 9 tracks the backend: build each portal as its APIs become available; all portals reuse the shared design system delivered early in phase 9 — which must already include the **branch switcher and restricted-result state from 14**.
- 10 (case/finance) after the core services produce the events/data it reads.
- **10b (claims) sits after 7 and alongside/after 10** — it needs **5/6** (`order_fulfillment`/`dispense_event` are the payable anchors), **2b** (provider contracts + tariffs for pricing), **7** (authorization linkage for gated lines), and **1** (`document-service` for invoices/receipts/settlement advice). It pairs with 10: claims produces the settlement advice, Finance executes payment **outside** the platform. Authoritative design: [../HBMP-Design/36-claims-management.md](../HBMP-Design/36-claims-management.md).
- **15 (call centre portal) is additive, not a retrofit** — it depends on **3** (the built appointment engine: slot lock, `Idempotency-Key`, `If-Match`), **2** (eligibility reception search), **1** (beneficiary contacts/identifiers), **8** (notification-service) and **9** (design system + portal catalog), **all of which are complete**, so it layers a new portal on existing infrastructure and rebuilds nothing. It adds exactly one service — `callcentre-service`, owning `call_interaction` + `caller_verification` — and **aggregates** the other services rather than forking or duplicating their data. It **pairs with 14**: the Call Centre role is **MemberScoped / all branches** (a central hotline), so when 14 lands its policy bundle simply sets `BranchUnrestricted` — branch and specialty stay *selectors*, never restrictions. Authoritative prompt: [phase-15-call-centre-portal.md](phase-15-call-centre-portal.md).
- 11 (hardening), 12 (migration/go-live), and the DPIA gate in 13 are **production-readiness gates** — see [../HBMP-Design/35-implementation-plan.md](../HBMP-Design/35-implementation-plan.md).

---

## What each phase file contains

- **Context to read** — the exact design docs to open first.
- **Prompt(s)** — copy-paste text for Claude Code, written in imperative form with explicit scope, constraints, and acceptance criteria (Given/When/Then where useful), cross-referencing the user stories in [../HBMP-Design/32-user-stories.md](../HBMP-Design/32-user-stories.md).
- **Guardrails** — invariants that must hold (e.g., atomic consume, min-necessary fields, immutable audit).
- **Done when** — the phase-specific exit criteria on top of the global Definition of Done.

---

## Non-negotiable invariants (repeated in every relevant prompt)

1. **Order-line consume is atomic, idempotent, and duplicate-proof** (unique constraint + optimistic concurrency + idempotency key). Partial fulfillment leaves remaining lines active. See [../HBMP-Design/23-state-machines.md](../HBMP-Design/23-state-machines.md).
2. **Minimum-necessary data** per role, enforced at row *and field* level ([../HBMP-Design/11-permission-matrix.md](../HBMP-Design/11-permission-matrix.md)) — reception ≠ EMR, labs ≠ prescriptions, pharmacies ≠ results, finance ≠ diagnoses, doctors see only patients they treat.
3. **Immutable, hash-chained audit** on every create/update/decision/consume/dispense/export ([../HBMP-Design/19-audit-strategy.md](../HBMP-Design/19-audit-strategy.md)).
4. **Accessibility (WCAG 2.2 AA) + Arabic RTL** are acceptance criteria for every UI story ([../HBMP-Design/21-accessibility-checklist.md](../HBMP-Design/21-accessibility-checklist.md)).
5. **Soft delete + history**, never hard delete of clinical/benefit data.

---

## Custom skills (activate per phase)

This build has 20 Mersal-specific Claude Code skills in [`../claude-code-skills/`](../claude-code-skills/00-SKILLS-INDEX.md) that encode the business rules, state machines, minimum-necessary zoning, and brand system. **At the start of each phase, activate the skills mapped to it** in the [skills index → Phase → skills mapping](../claude-code-skills/00-SKILLS-INDEX.md#phase--skills-mapping). Two are always-on: `mersal-platform-architect` and `refugee-healthcare-management`. The root `CLAUDE.md` enforces this. Install the skills under `.claude/skills/`; install generic engineering skills (PostgreSQL, OpenAPI, Terraform, OWASP, TDD, Mermaid…) from a marketplace.

## Files in this folder

- [`00-MASTER-PROMPT-LIST.md`](00-MASTER-PROMPT-LIST.md) — this index.
- [`00-CLAUDE-MD-AND-CONVENTIONS.md`](00-CLAUDE-MD-AND-CONVENTIONS.md) — root `CLAUDE.md` content: stack, repo layout, conventions, Definition of Done.
- `phase-0-foundations.md` … `phase-9-frontend-portals.md` — the phased prompts.
