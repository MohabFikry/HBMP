# Mersal HBMP — Custom Claude Code Skills

Twenty domain skills that make Claude Code (or Cowork) **consistently apply Mersal's healthcare, benefit-management, and refugee-care business rules** — knowledge no generic public skill has. Each skill is grounded in the design set (`../*.md`) and the confirmed Mersal palette/logo.

Each skill is a folder with a `SKILL.md` (YAML frontmatter `name` + `description`, then the domain knowledge, rules, entities/states/invariants, and references).

---

## How to install / use

**In Claude Code (recommended for the build):** copy each skill folder into your repository's `.claude/skills/` directory (or `~/.claude/skills/` for global). Claude Code auto-discovers them and activates a skill when your request matches its `description`.

```
your-repo/
  .claude/skills/
    mersal-platform-architect/SKILL.md
    beneficiary-lifecycle-management/SKILL.md
    ...
```

**In Cowork:** skills are managed under **Settings → Capabilities**. To share one, zip its folder with a `.skill` extension (the folder must contain `SKILL.md`) and install it there. (Skills can't be created/modified from inside a chat session — install them via Capabilities.)

**Trigger them** by matching the description, or invoke by name (e.g., "use the PBM Adjudication Engine skill to review this formulary logic").

---

## The 20 skills

| # | Skill | Slug | Use it for |
|---|-------|------|-----------|
| 1 | Mersal Healthcare Platform Architect | `mersal-platform-architect` | Whole-platform architecture, service boundaries, decisions |
| 2 | Beneficiary Lifecycle Management | `beneficiary-lifecycle-management` | Identity, registration→activation, status lifecycle |
| 3 | NGO Healthcare Operations | `ngo-healthcare-operations` | Mersal operating model, teams, provider network context |
| 4 | Patient Journey Designer | `patient-journey-designer` | 7-phase journey, touchpoints, hand-offs |
| 5 | Refugee Healthcare Management | `refugee-healthcare-management` | UNHCR/refugee identity, PDPL, min-necessary, bilingual access |
| 6 | Healthcare Policy & Eligibility Engine | `policy-eligibility-engine` | Coverage, limits, real-time eligibility |
| 7 | Clinical Workflow Designer | `clinical-workflow-designer` | EMR/SOAP, orders, prescriptions, coding |
| 8 | Appointment & Queue Management | `appointment-queue-management` | Scheduling, walk-in queue, no-show |
| 9 | Referral Management | `referral-management` | Referral lifecycle, loop closure |
| 10 | Case Management System | `case-management-system` | Case 360, assignment, escalations |
| 11 | Medical Claims Engine | `medical-claims-engine` | Claims capture & adjudication (roadmap) |
| 12 | Health Insurance & TPA Operations | `health-insurance-tpa-operations` | TPA, prior-auth, utilization, benefits |
| 13 | PBM Adjudication Engine | `pbm-adjudication-engine` | Formulary, DUR, step therapy, interactions |
| 14 | Provider Network Management | `provider-network-management` | Onboarding, contracts/tariffs, isolation |
| 15 | Healthcare Reporting & KPIs | `healthcare-reporting-kpis` | KPI catalog, read-models, accessible charts |
| 16 | FHIR Integration Architect | `fhir-integration-architect` | FHIR R4 mapping, HL7 readiness, adapters |
| 17 | Healthcare Database Architect | `healthcare-database-architect` | Schema, audit/history, RLS, idempotency |
| 18 | Healthcare UI/UX Designer | `healthcare-uiux-designer` | Design system, WCAG, RTL, Mersal brand/logo |
| 19 | Executive Dashboard Designer | `executive-dashboard-designer` | Leadership dashboards, accessible charts |
| 20 | Healthcare Business Rules Engine | `healthcare-business-rules-engine` | Declarative benefit/auth/formulary rules |

---

## Phase → skills mapping

Activate these skills for each build phase (see `../claude-code-prompts/`). Two skills are **always on**: `mersal-platform-architect` (architecture discipline) and `refugee-healthcare-management` (privacy & minimum-necessary).

| Phase | Activate |
|-------|----------|
| 0 Foundations | mersal-platform-architect, healthcare-database-architect |
| 0b Master data | healthcare-database-architect, pbm-adjudication-engine, clinical-workflow-designer |
| 1 Registration/Policy | beneficiary-lifecycle-management, policy-eligibility-engine, refugee-healthcare-management, healthcare-database-architect |
| 2 Eligibility/Reception | policy-eligibility-engine, health-insurance-tpa-operations, healthcare-uiux-designer |
| 2b Provider network | provider-network-management, health-insurance-tpa-operations |
| 3 Appointments | appointment-queue-management, patient-journey-designer, healthcare-uiux-designer |
| 4 Clinical/EMR/Orders | clinical-workflow-designer, healthcare-business-rules-engine, pbm-adjudication-engine |
| 5 Lab/Imaging | clinical-workflow-designer, healthcare-database-architect, provider-network-management |
| 6 Pharmacy | pbm-adjudication-engine, clinical-workflow-designer |
| 7 Approvals | health-insurance-tpa-operations, healthcare-business-rules-engine, medical-claims-engine |
| 8 Notifications/Reporting | healthcare-reporting-kpis, executive-dashboard-designer |
| 8b Admin/Platform | mersal-platform-architect, healthcare-business-rules-engine, ngo-healthcare-operations |
| 9 Frontend portals | healthcare-uiux-designer, executive-dashboard-designer, patient-journey-designer |
| 10 Case/Finance | case-management-system, healthcare-reporting-kpis, medical-claims-engine |
| 11 Hardening/NFR | healthcare-database-architect, mersal-platform-architect |
| 12 Migration/Go-live | beneficiary-lifecycle-management, provider-network-management, ngo-healthcare-operations |
| 13 Interoperability | fhir-integration-architect, mersal-platform-architect |

The root `CLAUDE.md` (`../claude-code-prompts/00-CLAUDE-MD-AND-CONVENTIONS.md`) instructs Claude Code to activate the phase's skills at the start of each session.

---

## Custom vs generic skills

The big categorized list (System Architecture, Backend, Frontend, DevOps, Security, Testing, etc.) is **best installed from a skills marketplace**, not hand-authored — those are general engineering skills that don't encode Mersal specifics. Install the ones you need (e.g., PostgreSQL, OpenAPI, TailwindCSS, Terraform, OWASP, TDD, Mermaid) via your marketplace/Capabilities.

**These 20 are custom** because they carry knowledge no public skill has: Mersal's benefit rules, the atomic-consume invariant, minimum-necessary role zoning, refugee-data protection, the confirmed brand system, and the exact state machines. Use the generic skills for *how to build*; use these for *what Mersal's rules are*.

---

## Guardrails every skill reinforces
- Atomic, idempotent, duplicate-proof order/prescription consumption; partial fulfillment leaves remainder active.
- Minimum-necessary field-level access (reception ≠ EMR, labs ≠ prescriptions, pharmacies ≠ results, finance ≠ diagnoses, doctors treat-only).
- Immutable, hash-chained audit on every mutation; soft-delete + history.
- Provider & tenant isolation; break-glass specially audited.
- WCAG 2.2 AA + Arabic RTL; confirmed Mersal palette + official logo.
- No external integration without a DPIA + data-sharing agreement.
