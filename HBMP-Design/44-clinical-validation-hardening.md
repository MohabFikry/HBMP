# 44 — Clinical Validation Hardening (DDI, dosing, drug–disease, ICD hierarchy)

> Back to [00-README-INDEX.md](00-README-INDEX.md) · Builds on [43-approval-engine-and-prescribing-support.md](43-approval-engine-and-prescribing-support.md)
> Build prompt: [claude-code-prompts/phase-28-clinical-validation-hardening.md](claude-code-prompts/phase-28-clinical-validation-hardening.md)

**What this is.** A clinical review of the validation engine built in phase 26, and the design changes needed to make it *clinically useful* rather than structurally correct. The architecture is sound. The clinical content, the interaction data model, and three specific behaviours are not.

---

## 0. What phase 26 actually built (verified)

**Good, and worth defending:**

- Five check kinds × five states, with `ClinicalState` having **no `Blocked` member** — clinical checks are structurally incapable of blocking (`Findings.cs:61-67`). Only `Finding.Benefit(...)` can block.
- `Fetched<T>` makes "the source was unavailable" unrepresentable as an empty list (`Fetched.cs:22-34`) — the failure mode that produces a false all-clear is designed out.
- Step-2 re-runs the whole engine server-side and ignores the client verdict (`Prescriptions.cs:71-108`), proven by `ForgedClientVerdictTests`.
- Append-only validation runs and override reasons; 30 engine tests including `NO_CLINICAL_CHECK_CAN_EVER_BLOCK` and "dead dependency → Unavailable, never Ok".
- A real ARIA combobox, five-state line chips with four cues each, bilingual findings with provenance.

**Not built, or built and empty:**

| | State |
|---|---|
| `masterdata.drug_interaction` | **Zero rows.** No seed, no loader, no write endpoint. Every line reports `NotChecked` from the curated source |
| Dosing rules | Fetcher is `_ = drugIds; _ = ct;` returning empty, provenance `"not-yet-configured"` (`HttpClinicalValidationPorts.cs:196-206`). No table, no API, no loader |
| Patient age / weight / renal function | **Never passed to the engine.** `ValidationRequest` carries encounter, lines, diagnoses, active meds — nothing about the patient |
| ICD hierarchy | Flat table. `parent_code` does not exist. The loader **reads chapter/block rows and discards the relationship** (`Mappers.cs:8-18`) |
| Duplicate therapy | Explicitly skipped: `if (a.DrugId == b.DrugId) continue;` (`PrescriptionValidator.cs:168`) |
| Drug–disease contraindication | Does not exist as a concept |
| Severity in the UI | Carried in the contract, **never read by the UI** — only interpolated into a message string |

## 1. The three defects that matter most

### 1.1 The allergy check silently always passes — fix this first

`masterdata` matches an allergy by building the drug's ATC ancestor chain and testing whether any recorded allergen **code** appears in it (`Program.cs:529-542`). The seeded allergen codes are `ALG-PENICILLIN`, `ALG-SULFA`, `ALG-CEPHALO` (`0002_seed_allergens.sql:6-20`) — **not ATC codes**. The comparison is therefore structurally incapable of ever being true.

The engine then renders **"Ok — no conflict with the 3 recorded allergies."**

This is the worst failure shape in clinical decision support: not a missing check, but a **false negative presented as a positive assurance**. A prescriber who reads "no conflict with the recorded allergies" reasonably concludes the allergies were checked. They were not. No test covers the matcher against real allergen codes.

Two fixes, both required: map allergens to ingredients/ATC properly, and — until that mapping exists for a given allergen — the check must return **`NotChecked`, never `Ok`**. The platform's own principle (absence of data is not a negative result) is already applied correctly to interactions and diagnoses; it was not applied here.

### 1.2 Interactions are keyed on products, and cannot be populated

`drug_interaction(drug_a_id uuid, drug_b_id uuid)` stores pairs at **product** level. The Egyptian catalogue is tens of thousands of products. Interactions are a property of **active ingredients**, not of trade names — so product-level encoding means every ingredient pair must be replicated across the cartesian product of every brand containing each ingredient. That is why the table is empty and would stay empty: it is not a data-entry backlog, it is an unpopulatable model.

**Interactions must be keyed on ingredient (or ATC-5), and products resolved to ingredients at check time.** Consequences that follow:

- **Combination products must decompose.** A co-amoxiclav product is two ingredients; each must be screened separately, and against each ingredient of every other line.
- One curated pair (`warfarin` × `NSAIDs`) then covers every brand of each, in both directions, forever.
- Class-level entries (ATC-4/5) express "all NSAIDs" without enumerating them — and survive new products entering the market, which product-level rows do not.

### 1.3 The authoritative path trusts the client for the diagnosis

Step 2 carefully re-runs every check server-side — and then takes the diagnosis list from `req.DiagnosisIcdCodes` (`Prescriptions.cs:77`). The server never reads the encounter's diagnoses from `emr`.

So a submission with an empty or altered diagnosis array changes what the engine concludes about indication and (once built) contraindication. It is a hole in the exact invariant phase 26 was built to enforce. **The server must fetch the diagnoses by `encounterId`**; the client's copy is display state.

## 2. Severity tiering — the difference between a safety tool and noise

Today every clinical finding is a `Warning` requiring an acknowledgement with a typed reason. All severities are treated identically.

This is the single best-documented failure mode in clinical decision support: override rates above 90% are routinely reported in the literature, and the mechanism is always the same — when a contraindicated combination and a trivial one demand the same click, clinicians learn to dismiss both. **Uniform alerting destroys the signal of the alerts that matter.**

Tier the response to the severity that already exists in the schema:

| Severity | Behaviour | Rationale |
|---|---|---|
| **Contraindicated** | Interruptive. Requires an explicit typed override reason. Recorded and visible to the approver | These are "do not co-prescribe" — rare, and worth stopping for |
| **Major** | Interruptive, acknowledgeable with a reason | Clinically significant, action usually needed |
| **Moderate** | **Inline, non-interruptive.** Visible on the line, does not gate submit | Awareness, not intervention |
| **Minor** | Collapsed by default, available on demand | Reference only |

Only Contraindicated and Major should gate submission. Moderate and Minor render inline. The four-cue rule still applies to each, and severity becomes a **first-class UI element** — a distinct chip — rather than a word buried in a sentence.

## 3. An alert must carry mechanism, consequence and action

*"Major interaction with clarithromycin"* tells a prescriber nothing they can act on. The fields that make an interaction alert actionable are well established:

| Field | Example |
|---|---|
| **Mechanism** | CYP3A4 inhibition |
| **Clinical consequence** | 10-fold increase in simvastatin exposure → rhabdomyolysis, acute kidney injury |
| **Management** | Suspend the statin for the antibiotic course, or use azithromycin |
| **Onset** | Rapid / delayed |
| **Documentation level** | Established / probable / theoretical |

`drug_interaction` has `severity` and a single `description`. Split it: `mechanism`, `clinical_effect`, `management`, `onset`, `evidence_level`, plus bilingual text. The management line is what converts an alert from an obstacle into advice — and it is the field most likely to change the prescription.

Same principle for every other check: a dose warning should state the applicable maximum and the source; a contraindication should state why.

## 4. Dose checking — what it actually requires

The current rule is `DoseAmount × TimesPerDay > MaxDailyDose` plus a duration ceiling. That is a reasonable adult, fixed-dose check and nothing more. A dose check that is safe across a refugee clinic population — which skews paediatric — needs:

- **Indication-specific ranges.** The same molecule is dosed differently for different conditions. The user's requirement ("recommended dose based on diagnosis") therefore needs the rule keyed on **(ingredient, indication category, population, route)** — the current `DosingRuleFact(DrugId, MaxDailyDose, DoseUnit, MaxDurationDays)` has no indication and no population dimension.
- **Weight, for paediatrics.** mg/kg is the only correct paediatric calculation. Weight exists in `emr.vital` (`Weight`, `Height`, `BMI`) and is **not passed to the engine**.
- **Age**, from the beneficiary record — for paediatric bands, neonatal exclusions and geriatric caution.
- **Renal function** for renally-cleared drugs. This is the honest limit: lab results are stored as `result_value text` (`orders/…/0002_fulfillment.sql:15`), so there is **no structured creatinine or eGFR**. Computing a renal adjustment is therefore impossible today.

**The rule that keeps this safe:** when a rule requires an input the platform does not have, the result is **`NotChecked` naming the missing input** — "weight required for paediatric dosing", "renal dosing applies; eGFR not available" — never `Ok`. A dose check that silently ignores renal function on a renally-cleared drug in a patient with kidney disease is worse than no check, because it reassures.

Display alongside the entered dose: **the recommended range for the selected indication and population**, with its source. That is the feature the request asked for, and it is more useful than a pass/fail — it teaches, and it makes the override decision informed.

## 5. Drug–disease contraindication — the check you actually want

The request said "compatibility of the medication with diagnosis". There are **two different checks** hiding in that phrase, and conflating them is why indication matching produces noise:

| | **Indication match** (built) | **Contraindication** (missing) |
|---|---|---|
| Question | Is this drug *used for* this condition? | Is this drug *dangerous in* this condition? |
| A mismatch means | Off-label — legitimate and common | Potential harm |
| Correct response | Low-value advisory | **High-value warning** |

Off-label prescribing is normal practice, so indication mismatch will warn constantly and be dismissed constantly. **Drug–disease contraindication is where the clinical value is**, and it does not exist yet.

A `drug_disease_contraindication(ingredient_or_atc, icd_scope, severity, mechanism, management)` table keyed to the same ICD hierarchy carries the classic pairs — NSAIDs in peptic ulcer disease, chronic kidney disease or heart failure; beta-blockers in asthma; metformin in significant renal impairment; ACE inhibitors in pregnancy; tetracyclines and fluoroquinolones in pregnancy and young children.

**Pregnancy deserves singling out.** For this population it is the single highest-yield check, it is a status rather than a lab, and it is cheap to capture. Note that the FDA replaced letter categories (A/B/C/D/X) with narrative labelling in 2015, so the model should carry a **structured pregnancy risk statement with its source**, not a letter grade.

**Also worth adding:** geriatric caution (Beers-criteria style), paediatric age floors, and cumulative-dose ceilings for the few drugs that need them.

## 6. ICD hierarchy — built properly, not by truncation

Today: `drug_indication.icd_code` holds an **undotted 3-character category** ("E11"); `emr.diagnosis.icd_code` holds the **dotted specific code** ("E11.9"); the engine truncates the encounter code to three characters and intersects (`PrescriptionValidator.cs:84-86, 106-110`). For the common case — drug indicated at category level, diagnosis coded at subcategory level — this *works*.

It breaks in four ways:

1. **Block-level indications.** "J00–J06" (acute upper respiratory infections) is a block, not a category. Truncation cannot express it.
2. **Indications more specific than three characters** are silently widened to their whole category.
3. **Chapter and block relationships exist in the source file and are thrown away** — the loader reads rows of `Type ∈ {chapter, block}` and uses them only to set `is_billable = false` (`Mappers.cs:8-18`). The hierarchy you need is already in the data you already load.
4. **The normalisation is duplicated** — `MasterDataNormalize.IcdCategory` and a second copy inside the validator. Two implementations of a matching rule will diverge.

**The fix:** add `parent_code` to `icd_code`, populate it from the chapter/block/category structure the loader already sees, and expose an ancestor/descendant lookup (a closure table, or `ltree`). Then the matching rule becomes a real one:

> A drug indication at node **L** matches an encounter diagnosis **D** if **D is a descendant-or-self of L**.

And handle the inverse honestly: if the encounter code is *less* specific than the indication (diagnosis "E11", indication "E11.9"), that is a **possible** match at lower confidence, not a miss and not a clean hit — report it as such. Keep dot-normalisation in exactly one place.

## 7. Duplicate therapy — cheap, high-yield, currently skipped

`if (a.DrugId == b.DrugId) continue;` skips the same product, and nothing checks two *different* products sharing an ingredient. Two trade names holding the same molecule is the commonest real-world prescribing duplication — and once interactions are ingredient-keyed (§1.2), detecting it is nearly free:

- **Same ingredient** across lines → Warning ("both contain paracetamol — check the combined daily total"). This is also the paracetamol-overdose path, which is worth catching on its own.
- **Same ATC-4 class** → Warning (two NSAIDs, two PPIs, two SSRIs).
- Both must account for **combination products**, where the duplication hides inside a compound.

## 8. Sourcing the clinical content without a licence

For a charity, the practical path is a **small, curated, high-yield list rather than comprehensive coverage** — and saying so in the UI.

- **Start from the published high-priority interaction lists.** The ONC/NLM high-priority DDI work (Phansalkar et al.) defines a short, citable set intended precisely for interruptive alerting. A few dozen ingredient-level pairs cover the majority of preventable harm.
- **Prioritise by what Mersal actually dispenses.** Derive the candidate list from the formulary and the top-N dispensed ingredients; that turns "curate 30,000 products" into "curate a few hundred pairs".
- **Highest-yield categories** for a primary-care refugee population: warfarin combinations; NSAID + ACEI/ARB + diuretic (the "triple whammy" acute kidney injury); CYP3A4 inhibitors with simvastatin; serotonergic combinations; QT-prolonging combinations; methotrexate with NSAIDs or trimethoprim; ACE inhibitors with potassium-sparing agents.
- **Every entry is pharmacist-reviewed and carries its citation.** `source` and `source_release` already exist on the table — make them mandatory.
- **State coverage honestly in the UI**: *"Checked against Mersal's interaction list (N ingredient pairs, updated <date>)."* A partial list is ethical to ship only when its partiality is visible.

On penicillin–cephalosporin cross-reactivity, one nuance worth encoding rather than the folklore: the historically quoted ~10% figure is not supported by modern evidence; cross-reactivity is low and depends on side-chain similarity rather than the beta-lactam ring. Blanket cephalosporin avoidance after a penicillin label causes real harm through inferior antibiotic choice. Encode side-chain-aware relationships, and mark the confidence.

## 9. Priority order

Ordered by clinical risk, not by effort:

1. **Allergy false-negative** — a check that reassures without checking (§1.1)
2. **Server-side diagnosis fetch** — closes the trusted-client hole (§1.3)
3. **Ingredient-level interaction model + seed the high-priority list** (§1.2, §8)
4. **Severity tiering** so contraindicated alerts stop competing with trivial ones (§2)
5. **Duplicate therapy** — cheap once §3 lands (§7)
6. **Mechanism / consequence / management on every alert** (§3)
7. **ICD hierarchy** proper (§6)
8. **Age + weight into the engine**; weight-based dosing; missing input → NotChecked (§4)
9. **Drug–disease contraindications, pregnancy first** (§5)
10. **Indication-keyed dosing with a displayed recommended range** (§4)

## 10. Invariants (additions to [43](43-approval-engine-and-prescribing-support.md) §8)

11. **No check returns `Ok` unless it actually evaluated.** A structurally impossible match, a missing mapping, or a missing patient input yields `NotChecked` naming what was missing.
12. **Interactions, contraindications and duplication are evaluated at ingredient level**, with combination products decomposed.
13. **Only `Contraindicated` and `Major` gate submission.** Moderate and Minor render inline and never block flow.
14. **Every clinical finding carries mechanism, consequence and management** where its source provides them, plus a citation.
15. **The server reads the diagnosis from the encounter**, never from the request body.
16. **ICD matching is a hierarchy walk**, in one implementation, with one dot-normalisation.

## 11. Acceptance criteria

- [ ] Allergy: allergens map to ingredients/ATC with side-chain-aware cross-reactivity; an unmapped allergen yields **NotChecked, never Ok**; a test asserts a penicillin-allergic patient prescribed amoxicillin gets a warning — the case that silently passes today.
- [ ] Interactions keyed on **ingredient/ATC**, products decomposed (combination products screen every ingredient); a seeded high-priority list produces real findings; coverage stated in the UI.
- [ ] Severity is a **first-class UI element**; Contraindicated/Major interrupt and gate submit, Moderate/Minor render inline and do not.
- [ ] Every interaction finding shows **mechanism, clinical effect, management** and a citation.
- [ ] **Duplicate therapy** detects same-ingredient and same-ATC-4 across lines, including inside combination products.
- [ ] `icd_code` has a populated hierarchy; matching is descendant-or-self; block ranges work; less-specific diagnoses report as possible matches; **one** dot-normalisation implementation.
- [ ] Age and weight reach the engine; weight-based rules apply for paediatric patients; a missing weight yields **NotChecked naming the missing input**; renal-adjusted drugs state that eGFR is unavailable rather than passing.
- [ ] Drug–disease contraindications evaluate against the ICD hierarchy; **pregnancy** is checked; a contraindicated pair is severity-tiered accordingly.
- [ ] The recommended dose range **for the selected indication and population** is displayed beside the entered dose, with its source.
- [ ] The server fetches diagnoses from `emr` by `encounterId`; a forged diagnosis array in the request changes nothing — proven by test.
- [ ] All phase-26 invariants still hold: no clinical check blocks, dead dependency yields Unavailable, step 2 ignores client verdicts.

---

### Cross-references
Engine design: [43](43-approval-engine-and-prescribing-support.md) · Benefit rules & formulary: [phase-27](claude-code-prompts/phase-27-approval-engine.md) · Build: [phase-28](claude-code-prompts/phase-28-clinical-validation-hardening.md)
