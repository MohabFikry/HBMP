# Phase 28 — Clinical validation hardening (DDI, dosing, drug–disease, ICD hierarchy)

**Goal:** Make the phase-26 validation engine *clinically useful*. The architecture is sound and must be preserved. What is missing is correct clinical content, an interaction model that can actually be populated, severity-appropriate alerting, and four checks that don't exist.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Design: [`../44-clinical-validation-hardening.md`](../44-clinical-validation-hardening.md)

> ⚠️ **Gate 1 is a live safety defect.** `masterdata` matches allergies by testing whether a recorded allergen **code** appears in the drug's ATC ancestor chain (`Api/Program.cs:529-542`), while the seeded allergen codes are `ALG-PENICILLIN`, `ALG-SULFA`, `ALG-CEPHALO` (`0002_seed_allergens.sql:6-20`). The comparison **can never be true**. The engine then renders *"Ok — no conflict with the N recorded allergies."* A false negative presented as a positive assurance is the worst failure shape in clinical decision support. Fix it before anything else.
>
> **Preserve what phase 26 got right.** `ClinicalState` has no `Blocked` member; `Fetched<T>` makes "source down" unrepresentable as empty; step 2 ignores the client verdict; `NO_CLINICAL_CHECK_CAN_EVER_BLOCK` is a test. **Do not weaken any of these.** Every gate below extends the engine; none relaxes it.

## Skills to activate
> **Superpowers:** use **brainstorming** before Gate 3 (the ingredient-level interaction model has real alternatives — ATC-5 vs ingredient id vs both — and the choice is hard to reverse); **writing-plans** for the ICD-hierarchy migration in Gate 7; **test-driven-development** throughout — every gate here is pure domain logic with a clear expected/actual, and the existing `PrescriptionValidatorTests` (30 facts) is the pattern to extend.
> **Project skills:** `mersal-platform-architect`, `refugee-healthcare-management` (always-on), `pbm-adjudication-engine`, `healthcare-business-rules-engine`, `healthcare-database-architect`, `clinical-workflow-designer`, `healthcare-uiux-designer`.

## Context — read first
- [`../44-clinical-validation-hardening.md`](../44-clinical-validation-hardening.md) — **AUTHORITATIVE**. §1 (the three defects), §2 (severity tiering), §4 (dose), §5 (contraindication), §6 (ICD hierarchy), §9 (priority), §10 (invariants).
- [`../43-approval-engine-and-prescribing-support.md`](../43-approval-engine-and-prescribing-support.md) §1 — benefit blocks, clinical warns. Unchanged.
- **Existing code:** `libs/clinical-validation/{PrescriptionValidator.cs,Findings.cs,Fetched.cs,ValidationInputs.cs}`, `services/pharmacy/{Api/PrescriptionValidationService.cs,Api/HttpClinicalValidationPorts.cs,Api/OpenFdaLabelSource.cs,Api/Prescriptions.cs}`, `services/masterdata/{Api/Program.cs,Domain/MasterDataNormalize.cs,Infrastructure/Migrations/*}`, `tools/masterdata-loader/{Mappers.cs,Loaders.cs,DbUpsert.cs}`, `apps/web/src/screens/prescribing/{PrescribingWorkspace.tsx,LineStatusChip.tsx,DrugCombobox.tsx}`, `services/emr/Infrastructure/Migrations/0005_clinical.sql` (vitals).
- **Run DB-gated tests with `./dotnet.sh test --with-db`.**

## INVARIANTS (../44 §10, additive to ../43 §8)
1. **No check returns `Ok` unless it actually evaluated.** Impossible match, missing mapping or missing patient input ⇒ `NotChecked` **naming what was missing**.
2. Interactions, contraindications and duplication evaluate at **ingredient level**; combination products decompose.
3. Only **Contraindicated** and **Major** gate submission. Moderate/Minor render inline and never block.
4. Every clinical finding carries **mechanism, consequence, management** where the source provides them, plus a citation.
5. The server reads the diagnosis **from the encounter**, never from the request body.
6. ICD matching is a **hierarchy walk**, one implementation, one dot-normalisation.

---

## Gate 1 — The allergy false-negative (do this first, ship it alone)

```text
services/masterdata/Api/Program.cs:529-542 builds AtcAncestors(drug.AtcCode) and tests whether any
recorded allergen CODE is in that chain. Seeded allergen codes are ALG-PENICILLIN / ALG-SULFA /
ALG-CEPHALO (0002_seed_allergens.sql:6-20). The test can never be true. The engine then reports Ok.

1.1 MAKE THE UNMAPPED CASE HONEST FIRST — this is a one-line-shaped change and it removes the false
    assurance immediately: if an allergen has no mapping to an ingredient/ATC, the allergy check returns
    NotChecked naming the unmapped allergen, NEVER Ok. Ship this before the mapping work if it is
    faster; a check that admits it did not run is safe, one that claims a clean result is not.

1.2 BUILD THE MAPPING:
    - masterdata.allergen gains: mapped_ingredient_ids uuid[] (or a join table), atc_scopes text[],
      cross_reactivity_group varchar(32) NULL, mapping_source, mapping_reviewed_by, mapping_reviewed_at.
    - Seed mappings for the 15 shipped allergens. Every mapping is pharmacist-reviewed and carries its
      source — an unreviewed mapping is not better than no mapping.
    - Matching evaluates: exact ingredient, then ATC scope, then cross-reactivity group.

1.3 CROSS-REACTIVITY, WITH THE EVIDENCE ENCODED (../44 §8):
    - Model it as SIDE-CHAIN-AWARE, not ring-based. The historically quoted ~10% penicillin/cephalosporin
      cross-reactivity is not supported by modern evidence; risk depends on side-chain similarity and is
      low, and blanket cephalosporin avoidance causes real harm via inferior antibiotic choice.
    - So cross_reactivity carries a CONFIDENCE (High/Moderate/Low/Theoretical) and the finding text must
      state it. A Low-confidence cross-reaction is a Moderate finding at most (Gate 4 tiering).
    - Do NOT encode sulfonamide-antibiotic → non-antibiotic-sulfonamide as a cross-reaction; that
      association is not supported and over-flagging it is a known CDS defect.

1.4 THE TEST THAT DOES NOT EXIST TODAY: a penicillin-allergic beneficiary prescribed amoxicillin gets a
    Warning. Assert it end to end (masterdata matcher + engine + API), not just against a hand-built
    AllergyScreen fixture — the current tests (PrescriptionValidatorTests.cs:194-228) never exercise the
    real matcher, which is why this shipped. Register in docs/quality/invariant-registry.yaml.
ACCEPTANCE: unmapped allergen ⇒ NotChecked; penicillin-allergic + amoxicillin ⇒ Warning; cross-reactivity
carries confidence; no seeded allergen silently passes.
```

## Gate 2 — Server-side diagnosis (close the trusted-client hole)

```text
Prescriptions.cs:77 and :260 take diagnoses from req.DiagnosisIcdCodes. The server NEVER reads the
encounter's diagnoses from emr. Step 2 re-runs every check and then trusts the client for its most
important input.

- Add an emr port: GET diagnoses by encounterId (reuse the caller's bearer, as the other ports do).
- Step 2 (POST /prescriptions) fetches diagnoses SERVER-SIDE and ignores req.DiagnosisIcdCodes entirely.
- Step 1 (POST /validate) may accept the client list for speed, but must mark the run's provenance as
  client-supplied so a step-1/step-2 divergence is explainable rather than mysterious.
- If emr is unreachable at step 2: the indication and contraindication checks are Unavailable — NOT
  NotChecked, and certainly not Ok. Distinguish "no diagnosis recorded" (NotChecked) from "could not
  read the diagnoses" (Unavailable).
- TEST: submit with a forged/empty DiagnosisIcdCodes against an encounter that HAS a contraindicated
  diagnosis; assert the server still produces the finding. Extend ForgedClientVerdictTests. Registry-pin it.
ACCEPTANCE: forged diagnosis array changes nothing at step 2; emr down ⇒ Unavailable, not Ok/NotChecked.
```

## Gate 3 — Ingredient-level interaction model (the reason the table is empty)

```text
Read ../44 §1.2. Use the superpowers brainstorming skill on the key first: ingredient-id vs ATC-5 vs
both. drug_interaction(drug_a_id, drug_b_id) is PRODUCT-level; the Egyptian catalogue is tens of
thousands of products; interactions are a property of ingredients. The table is not a data backlog, it
is an unpopulatable model.

MIGRATION (masterdata) — new model, migrate the old table out (it has zero rows, so no data loss):
- ingredient: ingredient_id, name_en, name_ar, rxcui NULL, atc_code NULL, is_active. Populate from
  masterdata.drug.scientific_name — normalise salts and INN/USAN spellings (OpenFdaLabelSource already
  has ingredient normalisation logic in LabelEvidence — REUSE it, do not write a second normaliser).
- drug_ingredient: drug_id, ingredient_id, strength, ordinal. COMBINATION PRODUCTS PRODUCE MULTIPLE
  ROWS — this is the point of the table.
- interaction_rule: rule_id, subject_kind CHECK IN ('Ingredient','AtcClass'), subject_value,
  object_kind, object_value, severity CHECK IN ('Minor','Moderate','Major','Contraindicated'),
  mechanism_en/ar, clinical_effect_en/ar, management_en/ar, onset CHECK IN ('Rapid','Delayed','Unknown'),
  evidence_level CHECK IN ('Established','Probable','Theoretical'), citation NOT NULL, source,
  source_release, reviewed_by, reviewed_at, is_active. UNIQUE on the unordered subject/object pair.
  Class-level entries (AtcClass 'M01A' = NSAIDs) express "all NSAIDs" in one row and survive new
  products entering the market — which product rows never do.
- A write/import endpoint + loader. Today there is NO way to get a pair into the system at all.

EVALUATION: resolve each prescribed product to its ingredient set, expand ATC ancestors per ingredient,
then match rules across the cartesian product of line-ingredient-sets (and against active medications).
Deduplicate by rule so a combination product does not raise the same rule twice.

SEED (../44 §8): the ONC/NLM high-priority interaction list (Phansalkar et al.) — a short, citable set
designed for interruptive alerting. Prioritise by what Mersal dispenses. Highest-yield for this
population: warfarin combinations; NSAID + ACEI/ARB + diuretic ("triple whammy" AKI); CYP3A4 inhibitors
+ simvastatin; serotonergic combinations; QT-prolonging combinations; methotrexate + NSAIDs or
trimethoprim; ACEI + potassium-sparing agents. EVERY row pharmacist-reviewed with a citation.

COVERAGE HONESTY: the API returns the rule count and last-updated date; the UI states
"Checked against Mersal's interaction list (N ingredient pairs, updated <date>)". A partial list is
ethical to ship only when its partiality is visible.
ACCEPTANCE: a co-amoxiclav product screens BOTH ingredients; one warfarin×NSAID class rule fires for
every brand of each in both directions; zero rules still yields NotChecked (unchanged); rule count is
surfaced.
TESTS: combination decomposition, class expansion, unordered matching, dedupe, coverage reporting.
```

## Gate 4 — Severity tiering (stop trivial alerts competing with contraindications)

```text
Read ../44 §2. Today EVERY clinical finding is a Warning requiring a typed acknowledgement. Override
rates above 90% are routinely reported in the CDS literature, and the mechanism is exactly this: when a
contraindicated pair and a trivial one demand the same click, both get dismissed.

- Contraindicated -> interruptive, explicit typed override reason, recorded and surfaced to the approver.
- Major          -> interruptive, acknowledgeable with a reason.
- Moderate       -> INLINE, non-interruptive, does NOT gate submit.
- Minor          -> collapsed by default.
Only Contraindicated and Major participate in `canSubmit`. Findings.cs RequiresAcknowledgement must
become severity-aware rather than "any Warning".

UI (PrescribingWorkspace.tsx / LineStatusChip.tsx): severity becomes a FIRST-CLASS element — its own
chip with four cues — not a word interpolated into a message (PrescriptionValidator.cs:211-213). The
line chip shows the WORST severity; the modal groups by severity then kind.
Keep the existing four-cue discipline and the Unavailable-≠-Ok distinction exactly as they are.
ACCEPTANCE: a Moderate interaction does not block submit and renders inline; a Contraindicated one
interrupts and requires a typed reason; severity is visible without opening the modal.
TESTS: gating matrix per severity, chip rendering per severity, axe EN+AR, no regression to the
five-state model.
```

## Gate 5 — Duplicate therapy

```text
Read ../44 §7. PrescriptionValidator.cs:168 skips same-drug pairs and nothing checks different products
sharing an ingredient. Two trade names holding the same molecule is the commonest real duplication —
and once Gate 3 lands, detecting it is nearly free.

- New CheckKind.Duplication.
- Same ingredient across lines (incl. inside combination products) -> Warning naming both products and
  the shared ingredient, with the combined daily total where doses are comparable. The paracetamol case
  is the one to get right — it is the classic accidental overdose and it usually hides in a combination
  product.
- Same ATC-4 class -> Warning (two NSAIDs, two PPIs, two SSRIs).
- Severity: Major for same-ingredient with a combined dose above a configured ceiling, Moderate otherwise.
ACCEPTANCE: two different brands of paracetamol warn, including when one is inside a cold-and-flu
combination; two NSAIDs warn; the same product twice warns (today it is silently skipped).
```

## Gate 6 — Mechanism / consequence / management on every finding

```text
Read ../44 §3. "Major interaction with clarithromycin" is not actionable. Gate 3 added the fields;
surface them everywhere and apply the same principle to the other checks:
- Interaction: mechanism, clinical effect, management, onset, evidence level, citation.
- Dose: the applicable maximum, the population it applies to, and the source.
- Contraindication: why the condition makes the drug hazardous, and what to do instead.
- Allergy: what matched (exact ingredient / ATC scope / cross-reactivity group) and at what confidence.
The management line is the field most likely to change the prescription — render it prominently, not in
a collapsed <details>. Keep provenance where it is.
ACCEPTANCE: every finding class renders an action, not just a label; bilingual throughout.
```

## Gate 7 — ICD hierarchy done properly

```text
Read ../44 §6. Today: drug_indication holds an UNDOTTED 3-char category; emr.diagnosis holds the DOTTED
specific code; the engine truncates to 3 chars and intersects. That works for the common case and fails
for block-level indications (J00-J06), for indications more specific than 3 chars, and it throws away
hierarchy the loader ALREADY SEES — Mappers.cs:8-18 reads rows of Type ∈ {chapter, block} and uses them
only to set is_billable = false.

- MIGRATION: masterdata.icd_code gains parent_code + node_kind CHECK IN ('Chapter','Block','Category',
  'Subcategory'), plus a closure/ancestor table (or ltree) with an index supporting descendant lookup.
  Populate from the structure the loader already reads. Handle block RANGES (J00-J06) explicitly —
  expand to member categories at load time, do not attempt range arithmetic at query time.
- MATCHING RULE: indication node L matches diagnosis D if D is DESCENDANT-OR-SELF of L.
  Inverse case (diagnosis less specific than the indication) is a POSSIBLE match at lower confidence —
  report it as such, neither a clean hit nor a miss.
- ONE NORMALISER: MasterDataNormalize is the single home for dot handling; DELETE the duplicate inside
  PrescriptionValidator.cs:106-110. Two implementations of a matching rule will diverge.
- Keep drug_indication's storage as-is (3-char category) but resolve through the hierarchy, so a future
  loader carrying 4-char indications works without a schema change.
ACCEPTANCE: E11.9 matches an E11 indication; J01.0 matches a J00-J06 block indication; an E11 diagnosis
against an E11.9 indication reports "possible, less specific"; exactly one dot-normalisation exists.
TESTS: descendant-or-self, block expansion, inverse case, dotted/undotted equivalence, no duplicate
normaliser (assert by grep in an architecture test).
```

## Gate 8 — Patient context: age and weight into the engine

```text
Read ../44 §4. ValidationRequest carries encounter, lines, diagnoses, active meds — and NOTHING about
the patient. Weight/Height/BMI exist in emr.vital (0005_clinical.sql:41-52) and are not wired.
Renal function does not exist as structured data (lab results are result_value text).

- ValidationRequest gains: PatientAgeYears/Months (from the beneficiary DOB), WeightKg (most recent
  emr.vital 'Weight' with its measured_at), IsPregnant (Gate 9), plus a RenalFunction slot that is
  EXPLICITLY Unknown today.
- Fetch server-side at step 2, same as diagnoses (Gate 2).
- STALE WEIGHT IS NOT A CURRENT WEIGHT: carry measured_at and treat a weight older than a configurable
  window (default 90 days for adults, 30 for children) as NotChecked-with-reason for weight-based rules.
  A two-year-old weight on a growing child is worse than none.
- MISSING INPUT ⇒ NotChecked NAMING IT: "weight required for paediatric dosing",
  "renal dosing applies; eGFR not available". NEVER Ok. This is invariant 1 and it is the whole safety
  argument for shipping partial dosing.
ACCEPTANCE: a paediatric patient with no recorded weight yields NotChecked naming weight; a stale weight
does the same; a renally-cleared drug states eGFR is unavailable rather than passing.
```

## Gate 9 — Drug–disease contraindications, pregnancy first

```text
Read ../44 §5. This is the check the request actually wanted, and it does not exist. Indication MISMATCH
is off-label and mostly noise; CONTRAINDICATION is harm.

- New CheckKind.Contraindication, distinct from Indication.
- MIGRATION: drug_disease_contraindication(rule_id, subject_kind Ingredient|AtcClass, subject_value,
  icd_scope (a hierarchy node from Gate 7), severity, mechanism_en/ar, clinical_effect_en/ar,
  management_en/ar, evidence_level, citation, source, reviewed_by, is_active).
- Evaluate the encounter's diagnoses (server-fetched, Gate 2) against the hierarchy (Gate 7).
- SEED the classic pairs, pharmacist-reviewed with citations: NSAIDs in peptic ulcer disease / CKD /
  heart failure; beta-blockers in asthma; metformin in significant renal impairment; ACE inhibitors in
  pregnancy; tetracyclines and fluoroquinolones in pregnancy and young children.
- PREGNANCY is the highest-yield single check for this population and is a STATUS, not a lab. Capture it
  as a structured field (with an explicit Unknown), and carry a structured pregnancy-risk statement with
  its source. NOTE: the FDA replaced letter categories (A/B/C/D/X) with narrative labelling in 2015 —
  model a statement + source, NOT a letter grade.
- Also add (lower priority, same table shape): paediatric age floors, geriatric caution
  (Beers-criteria style), cumulative-dose ceilings where relevant.
ACCEPTANCE: an NSAID prescribed to a patient with a coded peptic ulcer produces a severity-tiered
contraindication finding with management text; pregnancy status Unknown yields NotChecked, not Ok.
```

## Gate 10 — Indication-keyed dosing with a displayed recommended range

```text
Read ../44 §4. Current DosingRuleFact(DrugId, MaxDailyDose, DoseUnit, MaxDurationDays) has no indication
and no population; the fetcher is a hard-coded empty dictionary (HttpClinicalValidationPorts.cs:196-206).

- MIGRATION: dosing_rule(rule_id, subject_kind Ingredient|AtcClass, subject_value,
  indication_icd_scope NULL (null = any indication), population CHECK IN ('Neonate','Infant','Child',
  'Adolescent','Adult','Geriatric'), route, dose_unit, min_single, max_single, typical_daily, max_daily,
  max_duration_days, is_weight_based bool, mg_per_kg_min, mg_per_kg_max, weight_capped_at_adult_dose
  bool, renal_adjustment_note, hepatic_note, citation NOT NULL, source, source_release, reviewed_by).
- Rule selection: most specific match on (subject, indication scope, population, route). Ties resolve to
  the more specific indication scope; log ambiguity at authoring time.
- Weight-based rules compute against Gate 8's weight and cap at the adult maximum where flagged.
- DISPLAY THE RECOMMENDED RANGE for the selected indication and population beside the entered dose, with
  its source — this is the requested feature and it is more useful than pass/fail: it informs the
  override rather than just obstructing it.
- Keep the openFDA label prose as the fallback REFERENCE when no structured rule exists (that behaviour
  is already correct: state stays NotChecked and the text is explicitly not a comparison).
ACCEPTANCE: an adult and a paediatric patient on the same drug for the same indication get different
recommended ranges; the range is shown with its citation; no structured rule ⇒ NotChecked + label
reference, never Ok.
```

## Gate 11 — Docs, registry, ADR

```text
- ../22 gains ingredient, drug_ingredient, interaction_rule, drug_disease_contraindication, dosing_rule,
  allergen mappings, icd_code.parent_code + closure; ../23 gains the severity gating on the prescription
  lifecycle; ../11 unchanged (no new scopes expected — confirm); 00-README-INDEX + README gain doc 44.
- BUILD-STATUS gains 28.1-28.11.
- docs/quality/invariant-registry.yaml gains: allergy-unmapped-is-NotChecked, penicillin-allergy-warns,
  server-fetches-diagnosis, combination-products-decompose, only-major-and-contraindicated-gate,
  missing-weight-is-NotChecked, one-icd-normaliser.
- ADR-0028: ingredient-level interaction model and why product-level was abandoned; severity tiering and
  the alert-fatigue rationale; partial-coverage honesty in the UI; pregnancy as a structured status with
  a narrative risk statement rather than an FDA letter category.
- CLINICAL GOVERNANCE (state it in the ADR): every interaction rule, contraindication and dosing rule
  requires a named pharmacist reviewer and a citation before is_active = true. Enforce reviewed_by NOT
  NULL for active rows with a DB constraint — the same pattern as phase 27's activation constraint.
ACCEPTANCE: docs true; registry entries have named tests; ADR merged; no active clinical rule lacks a
reviewer and a citation.
```

---

## Guardrails
- **Never let a check say Ok when it could not evaluate.** This phase exists because that happened.
- **Clinical checks still never block** — severity changes *interruption*, not blocking. Only benefit rules (phase 27) block.
- **Ingredient level, always.** No product-level clinical rule may be added.
- **Every active clinical rule has a named pharmacist reviewer and a citation** — enforced by constraint.
- **State coverage in the UI.** A partial interaction list is ethical only when its partiality is visible.
- Preserve every phase-26 invariant: five states, `Fetched<T>`, `NO_CLINICAL_CHECK_CAN_EVER_BLOCK`, step-2 re-evaluation, four-cue chips, Unavailable ≠ Ok.
- Full suite green after each gate (`./dotnet.sh test HbmpPlatform.sln -c Release --with-db` + `pnpm -r test`).

## Done when
- [ ] Allergy: unmapped ⇒ NotChecked; penicillin-allergic + amoxicillin warns end to end; cross-reactivity is side-chain-aware with a stated confidence; no seeded allergen silently passes.
- [ ] Server fetches diagnoses from emr; a forged diagnosis array changes nothing; emr down ⇒ Unavailable.
- [ ] Interactions are ingredient/class-keyed, combination products decompose, the high-priority list is seeded with citations, and coverage is stated in the UI.
- [ ] Contraindicated/Major interrupt and gate; Moderate/Minor render inline; severity is a first-class chip.
- [ ] Duplicate therapy catches same-ingredient (incl. inside combinations) and same-ATC-4.
- [ ] Every finding carries mechanism, consequence, management and a citation; the management line is prominent.
- [ ] ICD hierarchy populated; descendant-or-self matching; block ranges work; inverse case reported as possible; exactly one dot-normaliser.
- [ ] Age and weight reach the engine; stale weight is not a current weight; missing inputs yield NotChecked naming them; renal states its unavailability.
- [ ] Drug–disease contraindications evaluate against the hierarchy; pregnancy captured as a structured status with a narrative risk statement.
- [ ] Recommended dose range for indication + population displayed with its citation.
- [ ] ADR-0028 merged; registry entries named; no active clinical rule lacks a reviewer and a citation.
