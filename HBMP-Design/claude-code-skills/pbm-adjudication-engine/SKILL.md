---
name: PBM Adjudication Engine
description: Pharmacy Benefit Management logic for Mersal HBMP — formulary, drug utilization review (DUR), drug–drug and drug–allergy interaction checks against the ATC-classified Egyptian drug master, step therapy, quantity limits, generic/therapeutic substitution, prior-auth for expensive drugs, and partial dispensing. Use when designing or reviewing any pharmacy benefit, prescribing-safety, or dispensing rule.
---

# PBM Adjudication Engine

## Purpose
Give Claude Code the Mersal Pharmacy Benefit Management model: how prescriptions are checked for safety and coverage at prescribing time and adjudicated at dispensing time, grounded in the client's **ATC-classified Egyptian drug master** and **WHO ATC/DDD** guidelines. Prescribing-safety checks (interactions/allergy) are v1 (`../../07-functional-requirements.md` FR-CLIN-007, FR-RX-002); full **PBM & formulary** is a roadmap train (R6+, `../../35-implementation-plan.md` §10) designed to attach to the pharmacy-service core.

## When to use / when not to use
- **Use when:** modelling formulary/coverage of drugs, DUR, drug–drug or drug–allergy interaction checks, step therapy, quantity/day limits, generic or therapeutic substitution, prior-auth for expensive/controlled drugs, or partial-dispense rules.
- **Do not use for:** the lab/imaging order path (different domain); the generic authorization workflow (use `health-insurance-tpa-operations`); post-dispense financial claims (use `medical-claims-engine`).

## Mersal domain knowledge & rules
- **Drug master & ATC.** Drugs come from `../../../Master Lists/Egyptian Drugs - ATC Classified.xlsx`, loaded into `masterdata.drug` (`drug_id`, `drug_code`, `name`, `atc_code` FK, `form`, `strength`) with `masterdata.atc_class` (`atc_code`, `title`, `level`) (`../../22-data-dictionary.md` §10.5). Use the **ATC hierarchy** (anatomical→therapeutic→pharmacological→chemical) for therapeutic-class grouping: substitution candidates, step-therapy tiers, and duplicate-therapy detection should be reasoned at the appropriate ATC level. Align quantity/dosing sanity checks with **WHO ATC/DDD** Defined Daily Doses from `../../../Master Lists/Raw Files/` (WHO ATC/DDD guidelines).
- **Interaction checks (DUR).** At prescribing time the system checks **drug–drug** and **drug–allergy** interactions and warns/blocks *per severity* (FR-RX-002, FR-CLIN-007). Severity enum is **`Minor | Moderate | Major | Contraindicated`** (`masterdata.drug_interaction`; `../../22-data-dictionary.md` §10.5, §11.4). Mersal handling: `Minor/Moderate` → soft warn (clinician may proceed with acknowledgement, audited); `Major` → hard warn requiring explicit override + reason; `Contraindicated` → block, route to prescriber/approval, never silently dispense. Drug–allergy checks read `emr.allergy` (severity Mild/Moderate/Severe) against `masterdata.allergen`.
- **Pharmacy never sees raw results.** A pharmacy receives the prescription + minimum beneficiary/eligibility context and a **server-computed derived safety flag** (e.g. `renal-adjust: yes`, `interaction: none`) — never raw `lab_result`/`imaging_result` values, which are `denied → derived` for the pharmacy role (`../../11-permission-matrix.md` §3.2/§4, §6.4; FR-RX-007). DUR that needs a lab value (e.g. renal dosing) must run server-side and expose only the flag.
- **Formulary & coverage (roadmap).** A formulary defines covered drugs and substitution rules referenced at dispensing (FR-MDM-009, FR-RX-006). A drug off-formulary or in a restricted tier ⇒ `NeedsAuthorization` (prior-auth for expensive/controlled drugs) or a formulary-alternative prompt, not a silent denial. Coverage decrements the `PHARMACY` `benefit_category` limit by `limit_type` (Annual/Count/…) at dispense.
- **Step therapy:** require documented trial/failure of a preferred (usually lower-cost, same-ATC-class) agent before a non-preferred one is covered; unmet step therapy ⇒ route to authorization with justification.
- **Quantity limits:** cap dispensed quantity per period against formulary/DDD-informed limits and against `quantity_prescribed`; `total dispensed ≤ prescribed` is an invariant (FR-INV-005; CHECK `0 ≤ quantity_dispensed ≤ quantity_prescribed`).
- **Substitution:** generic/therapeutic substitution is allowed **only within formulary/policy-approved alternatives**, recording original vs substituted drug + reason (FR-RX-006; `../../23-state-machines.md` §3 "substitution only from approved list"). Otherwise route to approvals.
- **Partial dispensing:** allowed on stock shortage — record quantity dispensed vs remaining, set `PartiallyDispensed`, leave unfilled lines `available` for a later visit; out-of-stock triggers backorder/partial without consuming the unfilled line (`../../23-state-machines.md` §3 pharmacy guards; FR-RX-005).

## Key entities, states & invariants
- **Entities:** `prescription`, `prescription_line` (`drug_id`, dose, route, frequency, `quantity_prescribed`, `quantity_dispensed`, `refills_allowed`), `dispense_event` (append-only, `idempotency_key` UNIQUE, `batch_no`); masterdata `drug`, `atc_class`, `drug_interaction`, `allergen` (`../../22-data-dictionary.md` §8, §10.5).
- **Prescription lifecycle:** `Draft → Submitted → (Approved | Rejected) → PartiallyDispensed → Dispensed` plus `Expired`, `Cancelled` (`../../23-state-machines.md` §3).
- **Invariants:** dispense is **atomic + idempotent** (same `idempotency_key` replays prior result, no double-dispense — FR-INV-001/004); quantity conservation (FR-INV-005); a presented `Expired/Cancelled/Rejected/Dispensed` prescription is **rejected and audited**; dispense only by the routed pharmacy (`PO`+`OST`, FR-INV-010); immutable audit on every dispense/decision.

## How to apply
1. At prescribing: resolve drug from `masterdata.drug`; run drug–drug (via `drug_interaction`) and drug–allergy checks; gate by severity (Minor/Moderate warn, Major override+reason, Contraindicated block).
2. Group by ATC class to catch duplicate therapy and to find substitution/step-therapy candidates; sanity-check quantities against WHO DDD.
3. At dispensing: check formulary coverage + `PHARMACY` limit; if off-formulary/restricted/step-unmet/expensive ⇒ route to authorization, don't deny silently.
4. Substitute only from the approved list, recording original vs substituted + reason.
5. Support partial dispense with quantity accounting; enforce `dispensed ≤ prescribed`; make each `dispense_event` atomic + idempotent.
6. Expose only derived safety flags to the pharmacy; run any result-dependent DUR server-side.

## Canonical references
- `../../../Master Lists/Egyptian Drugs - ATC Classified.xlsx` · `../../../Master Lists/Raw Files/` (WHO ATC/DDD)
- `../../22-data-dictionary.md` (§8 pharmacy, §10.5 masterdata drug/atc/interaction/allergen, §11.4 severity enum)
- `../../07-functional-requirements.md` (§6 RX, FR-CLIN-007, §12 MDM, §13 INV)
- `../../23-state-machines.md` (§3 prescription + pharmacy guards)
- `../../11-permission-matrix.md` (§3.2/§4/§6.4 pharmacy: prescription 🟠, lab/imaging results denied→derived)

## Guardrails
- Never dispense across a `Contraindicated` interaction; never dispense without acknowledging a `Major` interaction with a reason.
- Never expose raw lab/imaging results to the pharmacy — only server-computed safety flags.
- Never substitute outside the policy/formulary-approved list; always record original vs substituted + reason.
- Never let total dispensed exceed prescribed; never allow a second dispense on a replayed idempotency key.
- Never silently deny an off-formulary/expensive drug — route to prior authorization with justification.
- Reason about therapeutic equivalence and duplicate therapy via ATC class, not by drug name string matching.
