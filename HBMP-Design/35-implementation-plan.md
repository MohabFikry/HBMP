# 35 — Implementation Plan

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [29-delivery-plan.md](29-delivery-plan.md) · [33-sprint-roadmap.md](33-sprint-roadmap.md) · [27-risk-assessment.md](27-risk-assessment.md) · [20-compliance-checklist.md](20-compliance-checklist.md)

The master plan that ties the design set into an executable program: approach, team, governance gate, environments, data migration, training/change management, go-live, and success metrics.

> ⚠️ **Governance gate (mandatory):** implementation does not start until this complete design set — architecture, workflows, security, and specifications — is **reviewed and approved** by Mersal stakeholders (Medical, Operations, Security/DPO, Finance, IT). This plan begins *after* that sign-off.

---

## 1. Delivery approach
- **Agile (Scrum), 2-week sprints**, organized into phased releases R0–R5+ ([29-delivery-plan.md](29-delivery-plan.md), [33-sprint-roadmap.md](33-sprint-roadmap.md)).
- **Walking-skeleton first:** register → eligibility → consult → order/prescription → provider fulfilment → approval, thin but end-to-end, then widen ([28-mvp-definition.md](28-mvp-definition.md)).
- **Service-oriented HBMP core** so claims, PBM, inventory, and integrations attach later without re-platforming.
- **Secure/accessible/audited by default** — these are Definition-of-Done items every sprint, not a hardening phase.

---

## 2. Team & RACI

| Function | People (indicative) | Key responsibility |
|----------|--------------------|--------------------|
| Product Owner | 1 | Backlog, priorities, stakeholder liaison |
| Architect | 1 (part-time) | ADRs, service boundaries, standards |
| Backend engineers | 2–3 | Domain services (.NET on k3s) |
| Frontend engineers | 2 | Role portals (React/TS, RTL/a11y) |
| UX/Accessibility | 1 | Flows, wireframes, WCAG gate |
| QA | 1 | Test strategy, automation, UAT |
| DevOps/SRE | 1 | IaC, CI/CD, observability, DR |
| Security/DPO | 1 (part-time) | Threat model, authz, compliance |
| Tech writer | part-time | User/admin docs (AR/EN) |
| Clinical SME | part-time | Clinical validation, master data |

**RACI (high level):** Scope/priority — PO(A/R), stakeholders(C). Architecture — Architect(A/R), eng(C). Security/compliance sign-off — Security/DPO(A/R), legal(C). Release approval — Steering committee(A), PO(R). Go-live — SRE(R), PO(A).

---

## 3. Governance & approval gates
1. **Design sign-off (this baseline):** all stakeholders approve the 35-doc set before build.
2. **Per-release gate:** exit criteria met ([29-delivery-plan.md](29-delivery-plan.md)) + demo accepted.
3. **Compliance gate (pre-prod):** DPIA + RoPA + retention configured + legal sign-off on PDPL/cross-border ([20-compliance-checklist.md](20-compliance-checklist.md)).
4. **Security gate (pre-prod):** pen test findings resolved, authz tests green, break-glass audited ([18-security-model.md](18-security-model.md)).
5. **Go-live gate:** UAT sign-off, DR drill passed, training complete, migration validated.

A Steering Committee (Mersal Medical Director, Operations, IT/Security, Finance, PO) reviews at each gate.

---

## 4. Environments & release management
- Four isolated environments dev→QA→staging→prod ([25-deployment-architecture.md](25-deployment-architecture.md)); prod data never flows down unmasked.
- IaC-provisioned, GitOps deploys, progressive rollout (canary/blue-green) with automated rollback.
- DB migrations are backward-compatible (expand/contract) and gated.

---

## 5. Data migration & onboarding
The platform becomes the single source of truth, so onboarding existing data is a first-class stream:

| Stream | Source | Approach | Validation |
|--------|--------|----------|-----------|
| **Master data** | ICD-10/CPT/LOINC-ready, Drug/ATC, allergy DB | Load & version in admin; reconcile before clinical go-live | Counts + spot clinical review |
| **Providers** | Existing contracts/spreadsheets | Import provider, locations, contracts, users; validate isolation | Provider test logins scoped correctly |
| **Beneficiaries** | Existing records/paper | Staged import with identifier normalization + dedupe; assign policies/coverage | Sample reconciliation; eligibility spot-checks |
| **Historical clinical** (optional) | Legacy records | Attach as documents initially; structured import later | Access + min-necessary checks |

Migration principles: dry-run in staging with masked data, reversible loads, full audit of the migration, DPIA for the migration itself ([20-compliance-checklist.md](20-compliance-checklist.md)), and a cutover plan with a fallback.

---

## 6. Training & change management
- **Role-based training** (AR/EN) for each portal: Reception, Registration, Clinicians, Nurses, Lab/Imaging, Pharmacy, Approvals, Case Managers, Finance, Network, Admin.
- **Provider onboarding kit:** quick-start guides + short videos for external labs, imaging, pharmacies.
- **Champions/super-users** per team to support peers.
- Change plan addresses the shift from paper: sandbox practice, phased rollout per clinic, floor-walking support in week 1.
- Feedback loop into backlog; measure adoption (logins, feature usage, paper reduction).

---

## 7. Go-live & hypercare
- **Pilot-first:** launch at one clinic/branch, validate end-to-end, then roll out clinic-by-clinic (reduces risk vs. big-bang).
- **Hypercare (2–4 weeks):** elevated support, daily triage, fast-fix pipeline, on-call SRE, war-room for week 1.
- **Rollback plan:** documented per release; keep a manual fallback procedure until adoption is stable.
- Exit hypercare when incident rate, TAT, and adoption meet thresholds.

---

## 8. Success metrics (tie to OKRs in [01-product-vision.md](01-product-vision.md))
- **Adoption:** % visits processed digitally; paper forms eliminated; active users per role.
- **Efficiency:** eligibility check time; registration time; approval **TAT**; no-show rate.
- **Quality/safety:** % encounters with structured diagnosis; duplicate-order rate → ~0; audit completeness.
- **Access/inclusion:** accessibility conformance; AR/EN usage; provider portal uptake.
- **Reliability/security:** SLO attainment (99.9%+); zero unresolved criticals; security incidents; DR drill success.

---

## 9. Dependencies & assumptions
- Design sign-off obtained (gate 1).
- On-prem server (or VPS) provisioned in-country for PDPL residency; budget approved.
- Master data licensing/sources available (ICD/CPT/drug DB).
- Provider cooperation for onboarding and connectivity at clinics/labs/pharmacies.
- Legal counsel available for PDPL/cross-border and data-sharing agreements.
- Connectivity assumption at sites; offline-clinic support is a roadmap item, not v1.

---

## 10. Roadmap beyond v1 (R6+)
Sequenced as separate release trains once v1 is stable: full **PBM & formulary**, **Claims & billing**, **Inventory**, **Telemedicine**, **AI Clinical Decision Support**, **OCR + Arabic NLP**, **Patient/Provider mobile apps**, **Offline clinics**, **FHIR/HL7 interoperability**, **UNHCR/government/insurer integrations**, **digital referral network**. The service-oriented core ([16-service-architecture.md](16-service-architecture.md)) is designed so each is additive.

---

### Cross-references
- Releases: [29-delivery-plan.md](29-delivery-plan.md) · Sprints: [33-sprint-roadmap.md](33-sprint-roadmap.md) · Risks: [27-risk-assessment.md](27-risk-assessment.md)
- Compliance/security gates: [20-compliance-checklist.md](20-compliance-checklist.md) · [18-security-model.md](18-security-model.md) · Deployment: [25-deployment-architecture.md](25-deployment-architecture.md)
- Vision/OKRs: [01-product-vision.md](01-product-vision.md) · MVP: [28-mvp-definition.md](28-mvp-definition.md)
