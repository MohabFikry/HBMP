# Mersal Healthcare Benefit Management Platform (HBMP)
## Enterprise Design Program — Master Index

**Program codename:** PBM (Pharmacy/Patient Benefit Management) — positioned as a **Healthcare Benefit Management Platform (HBMP)**, not merely an NGO EMR.
**Client:** Mersal Foundation for Charity and Development (مؤسسة مرسال) — medical charity, Egypt.
**Document status:** DRAFT v0.9 — *balanced full first pass across all 37 deliverables, pending review & approval.*
**Last updated:** 2026-07-21

> ⚠️ **Do not begin implementation** until the complete architecture, workflows, and specifications in this workspace have been reviewed and approved by Mersal stakeholders.

---

### How this workspace is organized

This is a linked Markdown workspace. Every deliverable is its own file. Start here, then follow the links. Diagrams are authored in **Mermaid** and render in any Mermaid-aware viewer (GitHub, VS Code + Mermaid extension, Obsidian, Typora).

Read [`0A-DESIGN-FOUNDATIONS.md`](0A-DESIGN-FOUNDATIONS.md) **first** — it defines the shared vocabulary, tech stack, brand palette, and conventions every other document depends on.

---

### The 37 Deliverables

| # | Deliverable | File | Cluster |
|---|-------------|------|---------|
| — | Design Foundations (glossary, stack, palette, conventions) | [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md) | Core |
| 1 | Product Vision | [01-product-vision.md](01-product-vision.md) | A |
| 2 | Stakeholder Analysis | [02-stakeholder-analysis.md](02-stakeholder-analysis.md) | A |
| 3 | User Personas | [03-user-personas.md](03-user-personas.md) | A |
| 4 | Patient Journey Maps | [04-patient-journey-maps.md](04-patient-journey-maps.md) | A |
| 5 | Business Process Maps | [05-business-process-maps.md](05-business-process-maps.md) | B |
| 6 | BPMN Diagrams | [06-bpmn-diagrams.md](06-bpmn-diagrams.md) | B |
| 7 | Functional Requirements | [07-functional-requirements.md](07-functional-requirements.md) | C |
| 8 | Non-Functional Requirements | [08-non-functional-requirements.md](08-non-functional-requirements.md) | C |
| 9 | Information Architecture | [09-information-architecture.md](09-information-architecture.md) | C |
| 10 | Role Matrix | [10-role-matrix.md](10-role-matrix.md) | D |
| 11 | Permission Matrix | [11-permission-matrix.md](11-permission-matrix.md) | D |
| 12 | UI Wireframes | [12-ui-wireframes.md](12-ui-wireframes.md) | C |
| 13 | UX Flows | [13-ux-flows.md](13-ux-flows.md) | C |
| 14 | Navigation Structure | [14-navigation-structure.md](14-navigation-structure.md) | C |
| 15 | Database ERD | [15-database-erd.md](15-database-erd.md) | E |
| 16 | Service Architecture | [16-service-architecture.md](16-service-architecture.md) | E |
| 17 | API Specifications | [17-api-specifications.md](17-api-specifications.md) | E |
| 18 | Security Model | [18-security-model.md](18-security-model.md) | D |
| 19 | Audit Strategy | [19-audit-strategy.md](19-audit-strategy.md) | D |
| 20 | Compliance Checklist | [20-compliance-checklist.md](20-compliance-checklist.md) | D |
| 21 | Accessibility Checklist | [21-accessibility-checklist.md](21-accessibility-checklist.md) | C |
| 22 | Data Dictionary | [22-data-dictionary.md](22-data-dictionary.md) | E |
| 23 | State Machines | [23-state-machines.md](23-state-machines.md) | B |
| 24 | Sequence Diagrams | [24-sequence-diagrams.md](24-sequence-diagrams.md) | B |
| 25 | Deployment Architecture | [25-deployment-architecture.md](25-deployment-architecture.md) | E |
| 26 | Testing Strategy | [26-testing-strategy.md](26-testing-strategy.md) | F |
| 27 | Risk Assessment | [27-risk-assessment.md](27-risk-assessment.md) | F |
| 28 | MVP Definition | [28-mvp-definition.md](28-mvp-definition.md) | A |
| 29 | Phase-by-Phase Delivery Plan | [29-delivery-plan.md](29-delivery-plan.md) | F |
| 30 | Technical Backlog | [30-technical-backlog.md](30-technical-backlog.md) | F |
| 31 | Product Backlog | [31-product-backlog.md](31-product-backlog.md) | F |
| 32 | User Stories & Acceptance Criteria | [32-user-stories.md](32-user-stories.md) | F |
| 33 | Sprint Roadmap | [33-sprint-roadmap.md](33-sprint-roadmap.md) | F |
| 34 | Technical Documentation | [34-technical-documentation.md](34-technical-documentation.md) | F |
| 35 | Implementation Plan | [35-implementation-plan.md](35-implementation-plan.md) | F |
| 36 | Claims Management (Phase 10b) | [36-claims-management.md](36-claims-management.md) | E |
| 37 | Branch Scoping, Practitioner Specialty & Clinical Sensitivity (Phase 14) | [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md) | D |
| 38 | Policy & Member Administration (Phase 19) | [38-policy-member-administration.md](38-policy-member-administration.md) | D |
| 39 | Unified Patient Profile — role-projected 360 (Phase 20) | [39-patient-profile.md](39-patient-profile.md) | D |
| 40 | User & Access Model — membership as the principal, effective-set algebra (Phase 21) | [40-user-access-model.md](40-user-access-model.md) | D |
| 42 | Branch Management — coordinator & clinics manager, roster, licensing, clinic inventory (Phase 25) | [42-branch-management.md](42-branch-management.md) | D |
| 43 | Approval Engine, Benefit Lists & Prescribing Decision Support (Phases 26–27) | [43-approval-engine-and-prescribing-support.md](43-approval-engine-and-prescribing-support.md) | D |
| 44 | Clinical Validation Hardening — DDI, dosing, drug–disease, ICD hierarchy (Phase 28) | [44-clinical-validation-hardening.md](44-clinical-validation-hardening.md) | D |
| 45 | Encounter, Service History & Chronic Prescribing — Radiology rename, OP Procedures, per-line service history, acute/chronic refill windows, prescribing units, lowest-price & availability (Phase 29) | [45-encounter-and-prescription-adjustments.md](45-encounter-and-prescription-adjustments.md) | D |
| 46 | Order & Prescription Amendment and Cancellation — supersede-not-edit, the guarded transition, chronic duration/frequency edits, authorisation scope, propagation, order notes, the timeline from check-in (Phase 30) | [46-order-amendment-and-cancellation.md](46-order-amendment-and-cancellation.md) | D |
| 47 | Oversight & Analytics — the Medical Director's plane: the projection seam as a machine seam, tenant-on-envelope as the condition of a fact existing, cost at two grains, why oversight reads from reporting and never from the operational services, and the period every figure states (2026-08-11 audit) | [47-oversight-and-analytics.md](47-oversight-and-analytics.md) | D |
| 48 | Approvals & Claims Workbench — the claims worklist that read the wrong endpoint, the officer's write scopes with no screen, line adjudication with dual control and SoD, all six reconciliation buckets, and the break-glass retrospective review that could be entered and never exited (2026-08-11 audit) | [48-approvals-and-claims-workbench.md](48-approvals-and-claims-workbench.md) | D |
| 49 | Finance & the Counter — the HTTP client that never met its own schema, an export button with three controls that did nothing, the settlement lifecycle with no door, segregation of duties on the screen rather than in the refusal, and the out-of-stock flag that only ever existed in a fixture (2026-08-12 audit) | [49-finance-and-the-counter.md](49-finance-and-the-counter.md) | D |
| 50 | The Prescriber's Portal — the interaction check that reported Ok about a comparison it never made, the signed note with no correction path, a state the product could enter and not leave, notes on the one order kind that had none, and the chronic amendment that was built, debugged and unreachable (2026-08-20 audit) | [50-the-prescribers-portal.md](50-the-prescribers-portal.md) | D |
| 51 | The Counters — reception's eligibility verdict computed in the browser, a delivery counter whose one write had never worked, a "Verify & Deliver" screen with no identity to verify against, a referral loop nobody could close, a nurse's Results Inbox full of vitals, and the waiting room four phases of check-ins were writing into and nothing was reading (2026-08-20 audit) | [51-the-counters.md](51-the-counters.md) | D |
| 52 | Administration and Coordination — an access-review campaign nobody could review, a cross-tenant write the review table's RLS exemption did not cover, a break-glass register that dropped the count of out-of-scope uses, a network roll-up counted in the browser past the 403 that refuses it, a tier assignment that could be revoked and never made, and a case manager who could read everything and do nothing (2026-08-21 audit) | [52-administration-and-coordination.md](52-administration-and-coordination.md) | D |
| 53 | The Report Nobody Could Read — one route answering with an array on one branch and an object on the other, so a result dialog rendered four defaults against a real gateway; and a signed report or DICOM study that was scanned, encrypted, referenced and audited on upload, with no read path anywhere in the platform (2026-08-21) | [53-the-report-nobody-could-read.md](53-the-report-nobody-could-read.md) | D |
| 54 | The Check That Checked Nobody — an eligibility screen that searched on any fragment of a name and ran the check against `hits[0]`, so the plan, remaining cap and visit verdict on the card belonged to whichever member the database returned first; replaced by a verified lookup that takes an identifier the beneficiary presented plus part of their name, and refuses without naming anybody; and the same search silently capping its list at 25 rows while reporting that page length as the match count (2026-08-21) | [54-the-check-that-checked-nobody.md](54-the-check-that-checked-nobody.md) | D |

---

### Reading paths by audience

- **Executives / Mersal leadership:** 01 → 28 → 29 → 27 → 33
- **Product & BA:** 02 → 03 → 04 → 05 → 07 → 31 → 32
- **Architects & Engineers:** 0A → 16 → 15 → 17 → 23 → 24 → 25 → 30 → **37** → **39** (the profile is the one feature that aggregates every zone, so read it after the zones)
- **Clinical safety / prescribing:** 43 → 22 §8 → 23 §3 → ADR-0032 (why clinical checks warn and benefit rules block, and why interaction checking is local)
- **Security / DPO / Compliance:** 10 → 11 → 18 → 19 → 20 → **37** (branch scoping, special-category data, sensitive-result release) → **39** (the aggregation surface: server-side projection, the photo as biometric-adjacent data, clipboard as a disclosure)
- **Finance / Claims:** 10 → 11 → **36** → 31 (EPIC-13) → 32 (US-CLM-*) → 16
- **UX / Accessibility:** 03 → 09 → 12 → 13 → 14 → 21

---

### Source note on branding

Mersal Foundation details were confirmed via public sources ([Every.org](https://www.every.org/mersal), [arab.org](https://arab.org/directory/mersal-foundation/)). The brand palette in [`0A-DESIGN-FOUNDATIONS.md`](0A-DESIGN-FOUNDATIONS.md) was **sampled directly from Mersal's live website (mersal-ngo.org) rendered on 2026-07-21** — the true brand hues are **bright teal `#00ACAC`**, deeper teals (`#009091`/`#008080`/`#003737`), and a **gold/amber `#EDA827`** accent. Because the bright brand teal fails text contrast on white, the design tokens split brand hues (decorative) from WCAG-2.2-AA accessible action/text tokens. A final check against Mersal's formal print brand book (if one exists) is still advisable, but the UI can proceed on these confirmed colors.
