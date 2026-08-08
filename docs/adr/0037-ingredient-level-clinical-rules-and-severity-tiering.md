# ADR-0037 — Ingredient-level clinical rules, severity tiering, and clinical governance

- **Status:** Accepted
- **Date:** 2026-08-06
- **Phase:** 28
- **Design:** [`HBMP-Design/44-clinical-validation-hardening.md`](../../HBMP-Design/44-clinical-validation-hardening.md)
- **Supersedes in part:** ADR-0032 (prescribing validation engine), which stands except where noted

> The prompt for this phase named it ADR-0028. That number was taken by an unrelated decision before phase 28
> was written; the content is what matters and it is recorded here.

## Context

Phase 26 built a prescribing validation engine whose architecture is right — five states, `Fetched<T>` making
"the source was down" unrepresentable as an empty list, a step 2 that re-derives its own verdict, and a
`ClinicalState` with no `Blocked` so a clinical check cannot express a refusal.

Its clinical content was not right, in four ways that share one shape: **a check that could not run reported
that it had run cleanly.**

1. The allergy matcher compared an allergen CODE (`ALG-PENICILLIN`) against a drug's ATC ancestor chain
   (`J01C`). Disjoint code spaces; a constant false; and the engine rendering it as *"no conflict with the 3
   recorded allergies"* on every prescription this platform has ever issued.
2. `drug_interaction` keyed pairs on two product uuids against a 22,653-product catalogue. Zero rows.
3. Step 2 re-ran every check server-side and then read the diagnosis list from the request body.
4. Every finding, of every severity, demanded the same typed acknowledgement.

## Decisions

### D1 — Clinical rules are keyed on molecules, never on products

`interaction_rule`, `allergen_ingredient` and the duplicate-therapy check all key on
`ingredient.ingredient_key` — a normalised INN name — or on an ATC class. Products resolve to molecules at
check time through `drug_ingredient`.

**Rejected: product ids.** One clinical fact needed a row per pair of BRANDS. That is why the table was empty
and would have stayed empty; it was never a data-entry backlog.

**Rejected: ATC-5 as the only key.** 14.8% of the catalogue carries no ATC code, and a combination product
carries ONE ATC for the compound — co-amoxiclav is `J01CR02` and cannot decompose. The paracetamol-inside-a-
cold-remedy overdose would be undetectable by construction.

**Rejected: ingredient uuid.** Governance (D4) requires a named pharmacist to review every rule, and nobody
proofreads a uuid. The key is the molecule's name so the table can be read by the people accountable for it.

Consequence: one rule — `warfarin × M01A` — covers every brand of warfarin against every NSAID on the
market, in both directions, and keeps covering them as products come and go.

### D2 — Severity tiers interruption; it never tiers blocking

Only **Contraindicated** and **Major** interrupt and gate submission. **Moderate** renders beside the line;
**Minor** collapses. Contraindicated additionally requires a typed reason, recorded and surfaced to the
approver.

The evidence for this is the best-documented failure mode in clinical decision support: override rates above
90% are routinely reported, and the mechanism is always uniform alerting. When a contraindicated combination
and a trivial one demand the same click, clinicians learn to dismiss both.

**A null severity still interrupts.** Manufacturer-label interactions carry no grade because a label states
an effect rather than a rank; treating "ungraded" as "not serious" would be the engine inventing a clinical
judgement it has no source for.

This changes **interruption only**. `ClinicalState` still has no `Blocked`, and
`NO_CLINICAL_CHECK_CAN_EVER_BLOCK` still passes.

### D3 — The server reads the diagnosis from the encounter

Diagnoses moved **out of `ValidationRequest` and into `ValidationSnapshot`**, behind `Fetched<T>`, because
they are fetched data. Trusting the client is no longer expressible on the authoritative path: step 2 passes
an encounter id and no list at all.

Step 1 may still use the composing screen's copy — it is advisory and nothing is written from it — stamped
`ClientSupplied` so a step-1/step-2 divergence has a recorded explanation.

emr unreachable yields `Unavailable`, not `NotChecked` and never `Ok`. "No diagnosis is recorded" and "we
could not find out what is recorded" are different statements about different things.

### D4 — Clinical governance is a database constraint, not a convention

No interaction rule may be `is_active` without `reviewed_by` and `reviewed_at`
(`ck_interaction_rule_reviewed`). No allergen may carry an ATC scope without a named reviewer and a source
(`ck_allergen_mapping_reviewed`). `citation` is `NOT NULL` on every clinical rule table.

An unreviewed rule produces confident findings from unattributable judgement, which is worse than no rule.

### D5 — Cross-reactivity is side-chain aware, and states its confidence

The historically quoted ~10% penicillin/cephalosporin cross-reactivity figure is not supported by current
evidence: risk tracks R1 side-chain similarity rather than the shared beta-lactam ring.

This cuts both ways, and the second way is the one usually missed. Blanket cephalosporin avoidance after a
penicillin label causes **real harm** through inferior antibiotic choice — a refugee clinic reaching for a
second-line agent on a folklore percentage treats worse and pays more. So `confidence` is mandatory, it is
stated in the finding text, and a Low or Theoretical cross-reaction is capped at Moderate severity and does
not interrupt.

**Sulfonamide-antibiotic → non-antibiotic-sulfonamide is deliberately not encoded.** That association is not
supported by the evidence and over-flagging it is a documented CDS defect. Recorded here so it is not
"fixed" later.

### D6 — Partial coverage is shipped only when it is visible

A charity cannot licence a comprehensive interaction database. The curated list is seeded from the ONC/NLM
high-priority DDI work, prioritised by what Mersal dispenses, and the API returns the **rule count and the
last-updated date** so the finding can say *"checked against Mersal's interaction list: 13 ingredient pairs,
updated 6 Aug 2026"*.

"No interaction found" against a dozen curated pairs is a far weaker statement than the same words against a
licensed database. A partial list is ethical to ship only when its partiality is stated.

### D7 — A missing input is named, never assumed

A weight-based rule with no recorded weight reports *"weight is required for weight-based dosing"*. A weight
older than 30 days (child) or 90 days (adult) is a **missing** weight, not a current one: a two-year-old
weight on a growing child produces a confident mg/kg calculation against a number that stopped being true.

Renal function is modelled as an explicit `Unknown` and will stay `Unknown` until laboratory results are
stored as structured values — they are `result_value text` today. A dose check that silently ignores renal
clearance on a renally-cleared drug in a patient with kidney disease is worse than no check, because it
reassures.

### D8 — One implementation of each matching rule

Ingredient normalisation lives in `libs/ingredients`; ICD normalisation in `libs/clinical-codes`. Both are
dependency-free and I/O-free so the reference catalogue and the prescribing engine can share them without
either depending on the other. Architecture tests assert there is exactly one of each.

Two implementations of a matching rule diverge, and the divergence is silent: a pharmacist authors a rule
against one ingredient and a prescription is screened against another, with nothing reporting a problem.

## Consequences

- The catalogue must be reloaded for `drug_ingredient` and the ICD closure to populate for existing rows.
  Until then the checks fall back honestly — ATC-scope matching for allergies, category comparison for
  indications — and say what they could not evaluate.
- `masterdata.drug_interaction` is retained, empty, and commented as superseded. Nothing reads it.
- Every clinical finding now carries mechanism, clinical effect, management and a citation where its source
  provides them. The management line renders in the message rather than behind a disclosure, because it is
  the field most likely to change the prescription.

## Invariants added

Registered in `docs/quality/invariant-registry.yaml` with named tests:
`INV-ALLERGY-UNMAPPED-IS-NOT-CHECKED`, `INV-SERVER-READS-THE-DIAGNOSIS`, `INV-ONLY-SEVERE-FINDINGS-GATE`,
`INV-ONE-INGREDIENT-NORMALISER`, `INV-COMBINATION-PRODUCTS-DECOMPOSE`, `INV-INTERACTIONS-ARE-INGREDIENT-KEYED`,
`INV-DUPLICATE-THERAPY-DETECTED`, `INV-ICD-MATCHING-IS-A-HIERARCHY-WALK`,
`INV-MISSING-PATIENT-INPUT-IS-NOT-CHECKED`.

Every phase-26 invariant continues to hold.
