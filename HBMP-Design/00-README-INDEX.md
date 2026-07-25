# Mersal Healthcare Benefit Management Platform (HBMP)
## Enterprise Design Program — Master Index

**Program codename:** PBM (Pharmacy/Patient Benefit Management) — positioned as a **Healthcare Benefit Management Platform (HBMP)**, not merely an NGO EMR.
**Client:** Mersal Foundation for Charity and Development (مؤسسة مرسال) — medical charity, Egypt.
**Document status:** DRAFT v0.9 — *balanced full first pass across all 36 deliverables, pending review & approval.*
**Last updated:** 2026-07-21

> ⚠️ **Do not begin implementation** until the complete architecture, workflows, and specifications in this workspace have been reviewed and approved by Mersal stakeholders.

---

### How this workspace is organized

This is a linked Markdown workspace. Every deliverable is its own file. Start here, then follow the links. Diagrams are authored in **Mermaid** and render in any Mermaid-aware viewer (GitHub, VS Code + Mermaid extension, Obsidian, Typora).

Read [`0A-DESIGN-FOUNDATIONS.md`](0A-DESIGN-FOUNDATIONS.md) **first** — it defines the shared vocabulary, tech stack, brand palette, and conventions every other document depends on.

---

### The 36 Deliverables

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

---

### Reading paths by audience

- **Executives / Mersal leadership:** 01 → 28 → 29 → 27 → 33
- **Product & BA:** 02 → 03 → 04 → 05 → 07 → 31 → 32
- **Architects & Engineers:** 0A → 16 → 15 → 17 → 23 → 24 → 25 → 30
- **Security / DPO / Compliance:** 10 → 11 → 18 → 19 → 20
- **Finance / Claims:** 10 → 11 → **36** → 31 (EPIC-13) → 32 (US-CLM-*) → 16
- **UX / Accessibility:** 03 → 09 → 12 → 13 → 14 → 21

---

### Source note on branding

Mersal Foundation details were confirmed via public sources ([Every.org](https://www.every.org/mersal), [arab.org](https://arab.org/directory/mersal-foundation/)). The brand palette in [`0A-DESIGN-FOUNDATIONS.md`](0A-DESIGN-FOUNDATIONS.md) was **sampled directly from Mersal's live website (mersal-ngo.org) rendered on 2026-07-21** — the true brand hues are **bright teal `#00ACAC`**, deeper teals (`#009091`/`#008080`/`#003737`), and a **gold/amber `#EDA827`** accent. Because the bright brand teal fails text contrast on white, the design tokens split brand hues (decorative) from WCAG-2.2-AA accessible action/text tokens. A final check against Mersal's formal print brand book (if one exists) is still advisable, but the UI can proceed on these confirmed colors.
