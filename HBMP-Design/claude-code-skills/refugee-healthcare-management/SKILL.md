---
name: Refugee Healthcare Management
description: Refugee-specific identity, privacy, consent, and access rules for Mersal's beneficiaries under Egypt PDPL (Law 151/2020) and UNHCR data-protection alignment. Use when handling refugee beneficiary data, designing identity/registration, consent, data-subject rights, minimum-necessary access, or low-literacy/low-bandwidth/bilingual reach.
---

# Refugee Healthcare Management

## Purpose
Ensure everything Mersal builds treats refugee beneficiaries and their data with the dignity,
legal care, and access design that high-risk special-category health data demands. Mersal
(founded 2015) serves refugees and vulnerable people in Egypt; their health data is among the
most sensitive categories in law and the most consequential to get wrong.

## When to use / when not to use
- **Use when** modeling beneficiary identity/registration; deciding what data a screen or API
  may expose; designing consent capture, privacy notices, or data-subject-request (DSAR)
  workflows; reasoning about cross-border data flows; or making the product reachable to
  low-literacy, low-bandwidth, bilingual users.
- **Not for** org-role/workflow mechanics (`ngo-healthcare-operations`), visual system details
  (`healthcare-uiux-designer`), or KPI aggregation (`healthcare-reporting-kpis`) — though those
  must all honor the rules here.

## Mersal domain knowledge & rules
**Identity — refugees rarely hold a single National ID.** Beneficiary identity can be
established by any of: **National ID, Passport, Refugee ID, UNHCR Number, Organization Member
Number**. Internally every beneficiary has one immutable surrogate `beneficiary_id` (UUID v7)
and 0..n `beneficiary_identifier` rows (type + value + issuing authority + verification status).
Human business key: Member No `MRS-M-<10 digits>`. Never force a National ID as mandatory;
never treat a missing ID as ineligibility without the documented eligibility path.

**Legal frame (engineering aid, not legal advice — DPO validates).**
- **Egypt PDPL (Law No. 151 of 2020)** is the *primary* binding statute: controller processing
  personal data in Egypt; requires consent, a DPO, cross-border-transfer safeguards, and breach
  notification to the Data Protection Centre.
- **GDPR** is adopted as the *design baseline* (strongest standard); **HIPAA** as security/privacy
  *principles*. Neither is directly binding unless EU subjects / US covered entities are involved.
- **UNHCR Data Protection Policy** applies when refugee data is shared with or sourced from
  UNHCR: align identifiers, purpose limitation, and data-sharing agreements.
- Refugee health data = **special-category, high-risk** → a **DPIA is always required** for
  processing it at scale and for every new integration (UNHCR/gov/insurer).

**Core principles → how the platform meets them:**
- **Lawfulness/consent** — captured at registration; lawful basis (consent / vital interest)
  recorded per processing purpose; **missing mandatory consent blocks clinical use**.
- **Purpose limitation** — data used only for care/benefit administration, documented in RoPA.
- **Data minimization** — field-level minimum-necessary per role (Reception ≠ EMR, Finance ≠
  diagnosis, Labs ≠ prescriptions, Pharmacy ≠ lab results, Doctors = treated patients only).
  Enforced at data layer, not just UI.
- **Accuracy** — identifier verification status; edit + history tables.
- **Storage limitation** — retention schedule + soft-delete + purge; legal-hold overrides;
  no hard deletes of clinical/benefit data.
- **Transparency** — privacy notice in **AR + EN** shown at registration.

**Data-subject / beneficiary rights (all audited; requester identity verified first):**
Access (export), Rectification (with history), Erasure where lawful (subject to medical-record
retention + legal hold), Restriction/objection (consent withdrawal), Portability (FHIR-aligned
export), Info on processing (RoPA extract).

**Special-category clinical data (37 §5–6) — a tier above ordinary PHI.**
- **Categories.** `MentalHealth` is the **confirmed** Mersal requirement. `HIV_STI`, `Genetic`,
  `SubstanceUse`, `ReproductiveHealth`, and `GBV_Forensic` are the **proposed** remainder — they are
  the standard special-category set for refugee-serving health programmes and carry the same
  stigma/safety risk profile under Egypt PDPL and UNHCR data-protection norms. **Ratifying the final
  list is a Medical Director + DPO decision, and it is configuration, not code** — never hard-code a
  category policy that only an engineer can change.
- **Default-deny disclosure.** For a result classified `Sensitive`/`HighlySensitive`, full content is
  visible only to the **authoring/ordering doctor**; everyone else — including other treating
  clinicians, the **medical approval team**, case managers, and reporting — receives **existence
  metadata only** (category, date, status, ordering branch, `RESTRICTED` marker). This deliberately
  overrides the approval team's standing clinical oversight.
- **Release requires a justified request.** Mandatory `purpose_code` + free-text `justification`;
  decided by the authoring doctor **or** a Medical Director (flagged + extra-audited). Grants are
  **time-boxed** (default 72h `Sensitive` / 24h `HighlySensitive`), **single-result**,
  **non-transferable**, revocable, auto-expiring — and **every read under a grant is separately
  audited** with the grant id and purpose.
- **Break-glass is loud, not convenient.** Emergency access demands extra justification, immediately
  notifies the **authoring doctor + Medical Director + DPO**, and is flagged for mandatory
  retrospective review.
- **The beneficiary's own rights are unaffected.** Sensitivity gating restricts *staff* disclosure; it
  never narrows data-subject access, rectification, or portability for the beneficiary themselves.
- For refugees, a leaked mental-health, GBV, or HIV record can mean stigma, family rupture, or danger
  in the host community — treat these gates as safety controls, not paperwork.

**Dignity, access & non-discrimination (NGO-specific, not optional):**
- **Low literacy** — plain language, icons paired with text, no jargon-only status; never rely
  on reading dense text to complete a critical task.
- **Low bandwidth** — code-split portals (≤300 KB initial JS), graceful degradation, cached/
  read-only eligibility fallback so the front desk keeps working offline-degraded.
- **Bilingual & bidirectional** — full Arabic RTL + English LTR; bilingual master-data search
  (ICD/drug by AR or EN); notifications in the recipient's preferred language; correct bidi
  isolation for mixed AR/EN (e.g., Latin drug names).
- **Non-discrimination** — no field, default, or flow that stigmatizes refugee status,
  nationality, or condition; identity path never blocks a genuinely eligible beneficiary.

## Key entities/tokens/rules & invariants
- **Sensitivity tiers:** T1 restricted PII · **T2** sensitive PII/financial (UNHCR/registration
  ID, coverage, claims) · **T3** PHI/clinical (diagnoses, EMR, prescriptions, results) ·
  T4 platform-critical. Refugee identifiers are **T2**; health data is **T3** → both get read
  audit; T3 gets need-to-know ABAC + step-up.
- **Consent gate is a hard functional requirement** — no consent, no clinical processing.
- **Cross-border:** prefer an Azure region that keeps regulated data in Egypt/in-region unless a
  documented PDPL transfer basis exists.
- **Analytics use de-identified/pseudonymized data** — aggregates carry no direct identifiers
  unless explicitly authorized.
- **0 PHI/PII in logs** — structured redaction.

## How to apply
1. When touching beneficiary data, name its **sensitivity tier** and confirm the role's
   minimum-necessary field set — remove anything not needed for the task.
2. For identity, support **all** identifier types with verification status; never hard-require
   National ID.
3. Gate clinical processing on recorded **consent**; surface the AR/EN privacy notice at intake.
4. For any new data flow/integration, flag a **DPIA** and check cross-border residency.
5. Design the task to survive **low literacy, low bandwidth, and RTL** — treat these as
   acceptance criteria, not enhancements.

## Canonical references
- `../../20-compliance-checklist.md` (PDPL/UNHCR/GDPR mapping, RoPA, rights, DPIA, retention)
- `../../03-user-personas.md` (refugee beneficiary personas, literacy/bandwidth context)
- `../../21-accessibility-checklist.md` (RTL, low-literacy, reflow, non-color status)
- `../../0A-DESIGN-FOUNDATIONS.md` §2–3 (glossary, identifiers) · `../../11-permission-matrix.md`
- `../../37-branch-scoping-and-clinical-sensitivity.md` §5–6 (special-category classification,
  default-deny gating, justified release-request workflow, loud break-glass)

## Guardrails
- Not legal advice — Mersal's DPO + counsel validate PDPL applicability and cross-border posture.
- Never expand a field's visibility "for convenience"; minimum-necessary is enforced at the data
  layer and audited.
- Never let analytics, logs, notifications, or exports leak direct identifiers or PHI.
- Never design a registration or eligibility path that penalizes a refugee for lacking a
  National ID or for their nationality/status.
- Never disclose special-category clinical content without a recorded purpose + justification and an
  active, time-boxed, single-result grant — and never treat the approval team as an exception.
- Never hard-code the special-category list; it is Medical Director + DPO configuration, and never let
  sensitivity gating be read as a limit on the beneficiary's own data-subject rights.
