---
name: NGO Healthcare Operations
description: Mersal's operating model as a refugee-serving Egyptian medical charity — its contracted provider network, internal teams, benefit-administration core, and single-source-of-truth goal. Use when reasoning about Mersal operations, org roles, team workflows, who-does-what, or how a request moves across registration, approvals, providers, and finance.
---

# NGO Healthcare Operations

## Purpose
Give Claude the shared mental model of how Mersal actually runs so any feature, screen,
or workflow is grounded in the real operating model — not a generic hospital app. Mersal
is an Egyptian medical charity (founded 2015) that funds and coordinates care for refugees
and vulnerable people. The platform is a **Healthcare Benefit Management Platform (HBMP)**:
a benefit-administration core with clinical/EMR workflows sitting on top, not a single-clinic
management system.

## When to use / when not to use
- **Use when** deciding which team owns a step; modeling org workflows (intake → eligibility
  → care → orders → approval → fulfillment → claims); mapping a role to a portal; reasoning
  about internal vs. external actors; or explaining the charitable/benefit context.
- **Not for** pure visual/UI decisions (use `healthcare-uiux-designer`), refugee data-privacy
  specifics (use `refugee-healthcare-management`), or KPI/report design (use
  `healthcare-reporting-kpis`).

## Mersal domain knowledge & rules
**Operating model (TPA-like benefit administrator).** Mersal effectively plays the
Third-Party Administrator role: it contracts an external **provider network** (clinics,
doctors, laboratories, imaging centers, pharmacies), verifies beneficiary **eligibility**
against a **Policy** (coverage rules, limits, validity window), and administers
**authorizations/approvals** before high-cost or controlled services. Care is charitable —
the beneficiary is not billed; Mersal settles with providers.

**Reusable core "spine":** Beneficiaries · Eligibility · Coverage/Policy · Provider Network ·
Authorizations/Approvals · Orders · Prescriptions. Clinical/operational domains sit on top:
EMR · Appointments · Lab & Imaging · Pharmacy · Notifications · Reporting · Documents · Audit.
This layering exists so Mersal can later add claims, capitation, PBM, and UNHCR/gov/insurer
integrations without re-platforming.

**Internal teams (Mersal-side)** — each has its own portal, minimum-necessary scope:
- **Beneficiary Management** — intake, registration, identity verification, household linkage,
  benefit enrollment. Sees registration IDs (T2), *not* diagnoses.
- **Reception** — front-desk check-in, appointments, queue, identity + eligibility verdict
  (green/red light only). HARD RULE: **cannot view the EMR**.
- **Call Center** — remote support, appointment help, coverage-balance questions, complaint
  intake, triage routing. Sees coverage balances, not diagnoses.
- **Doctors / Nurses** — clinical care for **only patients they are actively treating**
  (treating-relationship gate). Doctors author diagnoses, orders, prescriptions, referrals,
  and raise approval requests; nurses record vitals/observations, cannot author diagnoses.
- **Medical Approval** — utilization review; the *only* non-treating role with broad clinical
  read, justified by adjudication purpose and offset by heavy PHI-read audit.
- **Medical Directors** — clinical governance, escalations, appeals, overrides (dual-control).
- **Case Managers** — coordinate complex/chronic/vulnerable beneficiaries across their
  assigned case load.
- **Finance** — claims, invoicing, provider payments, reconciliation. HARD RULE: **cannot view
  diagnoses**; payment initiate and release are separate people (SoD).
- **Network Team** — provider onboarding, contracting, credentialing, catalog/pricing.
- **Org Admin / Super Admin** — manage *who can access*, not the clinical data itself.

**External actors (provider-side):** Provider Admin (administers one provider org, strong
isolation) plus Labs, Imaging, Pharmacies — each sees only orders routed to them, with the
clinical *indication* but not unrelated EMR history.

**Goals that shape every decision:**
1. **Single source of truth** — one beneficiary record, canonical status taxonomy, no
   duplicate/parallel spreadsheets.
2. **Paperwork reduction** — replace paper approvals/orders/dispensing logs with auditable
   digital flows; an order line is atomically "consumed" so it cannot be reused.
3. **Minimum-necessary by role** — a first-class constraint, enforced at row + field level.

## Key entities/tokens/rules & invariants
- **Beneficiary** (canonical subject; "patient" is a clinical UI synonym) · **Member** (benefit
  capacity) · **Policy** · **Eligibility** (real-time yes/no) · **Provider** · **Order** ·
  **Prescription** · **Authorization** · **Encounter/Visit**.
- **Segregation of Duties (SoD):** no one both originates and approves the same sensitive
  transaction (e.g., a treating doctor cannot adjudicate their own approval request; payment
  initiate ≠ release; beneficiary create ≠ merge-approve).
- **Consume = atomic + idempotent:** one order line, one fulfillment, duplicate usage impossible.
- **Immutable audit; no hard deletes** of clinical/benefit data (soft-delete + history).
- Timestamps stored UTC, displayed `Africa/Cairo`.

## How to apply
1. Identify the actor's **role and portal** and its scope (`tenant:own`, `provider:own`,
   `beneficiary:treating/assigned`, `self`, `global`).
2. Trace the request across the spine: eligibility → (approval if required) → order/prescription
   → provider fulfillment → claim. Name the owning team at each hop.
3. Enforce the hard data-zoning rules and SoD before proposing any cross-team shortcut.
4. Default to the single-source-of-truth and paperwork-reduction goals when there's a tradeoff.

## Canonical references
- `../../01-product-vision.md` · `../../02-stakeholder-analysis.md` (mission, stakeholders)
- `../../10-role-matrix.md` (roles, scopes, SoD, capability grid) · `../../11-permission-matrix.md`
- `../../0A-DESIGN-FOUNDATIONS.md` §1–2, §6–7 (positioning, glossary, lifecycles)
- Prototypes: `../../prototype-hbmp-multiscreen.html`, `../../prototype-approvals-worklist.html`

## Guardrails
- Never propose giving a role data outside its documented scope to "make it easier."
- Reception ≠ EMR; Finance ≠ diagnosis; Labs/Imaging ≠ prescriptions; Pharmacy ≠ lab/imaging
  results; Doctors = treated patients only; Approval = clinical read by purpose. These are
  non-negotiable.
- Don't reframe Mersal as a billing/insurance product — care is charitable; the platform
  administers benefits and reduces paperwork, it does not charge beneficiaries.
- Defer legal/regulatory specifics to `refugee-healthcare-management` and the DPO.
