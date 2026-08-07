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

| 19 | [phase-19-policy-member-administration.md](phase-19-policy-member-administration.md) | **Real Policy Administration System:** payers/sponsors → plans with **effective-dated immutable versions** carrying benefit configuration (limits, co-pay, waiting periods, pre-auth triggers, exclusions) → policies → member groups → enrollment (dependents, waiting periods, terminations, retro-effective events, bulk import) with **coverage generated from the plan version** → **network tiers owned by network administration** with tier-aware cost-share → **multiple plans under a policy** with member plan election → **utilization for individual/group/plan/policy/payer** → **policy query + member query** → **bulk upload + data extract (as-of dated)** → **analytical dashboard (6 views, shared filters, drill-down)** → full coverage & administrative 360 → **signed, timestamped, cancellable notes**, **classified documents (policy paperwork + past medical history)** and a **change timeline** on policy and member | **38**, 22, 23, 11, 36, 37, 0B | — | after 18 Gate A |

| 20 | [phase-20-patient-profile.md](phase-20-patient-profile.md) | **Unified patient profile (role-projected 360):** one canonical 15-section contract — identity + **photo**, alerts, coverage & eligibility, past medical history, encounters, investigations & results, prescriptions, authorizations, referrals, documents, notes, financial, case, timeline, **call history (inbound/outbound + per-call summary + audited server-generated copy-to-clipboard)** — **composed server-side under the caller's token** (never a service account), projected per role, consolidating the four existing partial 360s; patient context bar across modules, search → profile, permission-gated module deep-links, audited `ProfileViewed` with the served section list | **39**, 11, 37, 38, 15, 19, 0B, 21 | — | after 18 Gate B + 19.3b/c |

| 21 | [phase-21-user-access-model.md](phase-21-user-access-model.md) | **User & access model:** `tenant_membership` as the security principal (identity ≠ membership ≠ tenant), tenant-local roles from templates, per-membership Allow/Deny overrides with deny-wins set algebra, **time-bounded attributed branch-scope grants** with a precedence chain + sentinel fail-closed predicates, program enablement (features + live-count caps, distinct error types), precomputed claims **within the frozen token contract** + out-of-session evaluator with parity tests, deprecation lifecycle, ambient membership attribution, access-review snapshot, guarded membership switching. **Adaptations A1–A6 in doc 40 §0 are normative — no PHI wildcard, nothing silent** | **40**, 10, 11, 18, 37, 19 | — | after 17 + 18 Gates B/C |

| 25 | [phase-25-branch-management.md](phase-25-branch-management.md) | **Branch management:** `branch_coordinator` + `clinics_manager` sharing **one permission set**, differing only in reach (new `BranchSetScoped` mode — manager sees all six clinics at once, coordinator one) · branch-scoped practitioner/specialty/**licence** administration without network-wide `provider:write` · **licence expiry enforced as at the slot date** (blocks generation + booking, flags existing appointments, never cancels, never retroactive) · `roster_exception` (leave/holiday/closure/ad-hoc) with **one availability computation** and impact preview · **clinic inventory** medical/non-medical on an append-only movement ledger, no `quantity_on_hand`, **no beneficiary identifier anywhere** · one Branch Management portal for both roles | **42**, 37, 40, 10, 11 | — | after 23; needs D1–D5 sign-off |

| 26 | [phase-26-prescribing-workspace.md](phase-26-prescribing-workspace.md) | **Prescribing workspace & clinical validation (doctor side):** `drug_indication` loaded from `egyptian-drug-list_5.xlsx` (no drug↔ICD link exists today) · drug search by **trade name or active ingredient** · ARIA combobox showing ingredient + price under the trade name · multi-line Rx with dose/**duration**/quantity · validation engine with **five** states incl. **Unavailable ≠ OK** (deletes the three silent catches that render an outage as a clean bill of health) · interactions checked **locally** (the free NLM API was discontinued Jan 2024) · server re-validates and ignores client verdicts · card-number retrieval built properly (min-necessary view, second identifier, audited) | **43**, 22, 23, 11 | — | first — the doctor-side ask |
| 27 | [phase-27-approval-engine.md](phase-27-approval-engine.md) | **Approval engine, benefit lists, rules & supervisor portal:** ONE `benefit_list` model for **Formulary / Exclusion / Escalation**, versioned, effective-dated, immutable once Active, attachable to **payer/policy/plan/group** (UNHCR formulary → group) with deterministic precedence (**exclusion beats formulary**) · rule engine over a **closed** fact/operator/action vocabulary — no expression language · **activation impossible without a simulation and an independent reviewer, enforced by DB constraint** · reason-code vocabulary shared with claims · step-2 authoritative re-evaluation · `approval_supervisor` with authoring scopes separable from PHI | **43**, 38, 36, 40, 11 | — | after 26 |

| 28 | [phase-28-clinical-validation-hardening.md](phase-28-clinical-validation-hardening.md) | **Clinical validation hardening:** fixes the **allergy check that always passes** (seeded `ALG-*` codes can never match an ATC chain, yet the engine reports "Ok — no conflict") · server-side diagnosis fetch (step 2 currently trusts the client) · **ingredient-level interaction model** replacing the unpopulatable product-level table, with combination-product decomposition and a seeded high-priority list · **severity tiering** so Contraindicated/Major interrupt while Moderate/Minor render inline · duplicate therapy · mechanism/consequence/**management** on every finding · real **ICD hierarchy** (descendant-or-self, block ranges) replacing 3-char truncation · age + weight into the engine, missing input ⇒ NotChecked · **drug–disease contraindications incl. pregnancy** · indication-keyed dosing with a displayed recommended range | **44**, 43 | — | after 26; Gate 1 is a live safety defect |

| 29 | [phase-29-encounter-and-chronic-prescribing.md](phase-29-encounter-and-chronic-prescribing.md) | **Encounter, service history & chronic prescribing:** **full** Imaging→Radiology rename (role, scopes, enums, events, routes) via expand→backfill→switch→contract with a dual-accept window for unexpired tokens and in-flight outbox events — **audit chain never rewritten** · **OP Procedures** as a fourth `order_type` reusing `orders-service`; **all remaining CPT categories orderable**, with **E/M routed to a Referral** · **External Provider Portal** for physiotherapy centres and outside clinics — provider-owned rows from commit one, multi-session partial consumption, mandatory referral loop closure · OP Procedures in History · **per-line service-history modal** (one endpoint, one component, server-projected, **sensitivity-gated**, audited, trends where permitted) · **acute/chronic prescriptions** with supervisor-configurable refill frequency, window allocation **summing exactly to the total** (100/3 → 34/33/33), one authorisation + per-dispense eligibility + per-dispense limit consumption, missed window forfeited ≠ blocked · **prescribing unit / pack size / splittability** on the drug master · **lowest-price per prescribing unit** within ingredient+strength+form · `availability` defaulting to **Unknown** | **45**, 43, 44, 37, 39 | — | after 28 |

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
