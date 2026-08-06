# Phase 28 — Clinical validation hardening: design

> Build prompt: [`HBMP-Design/claude-code-prompts/phase-28-clinical-validation-hardening.md`](../../../HBMP-Design/claude-code-prompts/phase-28-clinical-validation-hardening.md)
> Authoritative design: [`HBMP-Design/44-clinical-validation-hardening.md`](../../../HBMP-Design/44-clinical-validation-hardening.md)
> Engine it extends: [`HBMP-Design/43-approval-engine-and-prescribing-support.md`](../../../HBMP-Design/43-approval-engine-and-prescribing-support.md)

## What this is

Phase 26 built a validation engine whose *architecture* is right and whose *clinical content* is not.
This spec records how the eleven gates of phase 28 are built on top of it without weakening a single
phase-26 invariant, and what was decided where the build prompt left a real choice open.

Nothing here relaxes the engine. `ClinicalState` still has no `Blocked`; `Fetched<T>` still makes "the
source was down" unrepresentable as an empty list; step 2 still ignores the client's verdict;
`NO_CLINICAL_CHECK_CAN_EVER_BLOCK` still passes. Every gate below is additive to those.

## The defect this phase exists for

`services/masterdata/Api/Program.cs:539` decides whether a drug conflicts with a recorded allergy like
this:

```csharp
var conflict = codes.Any(c => atcChain.Contains(c));
```

`codes` are allergen codes — `ALG-PENICILLIN`, `ALG-SULFA`, `ALG-CEPHALO`
(`0002_seed_allergens.sql:6-20`). `atcChain` holds ATC codes — `J`, `J01`, `J01C`, `J01CA`. The two
sets are disjoint by construction, so the expression is **incapable of ever being true**.

`PrescriptionValidator.cs:263` then renders the result:

> *"No conflict with the 3 recorded allergy/allergies."*

A prescriber who reads that reasonably concludes the allergies were checked. They were not. This is a
false negative presented as a positive assurance, which is the worst failure shape in clinical decision
support — worse than an absent check, because an absent check does not reassure. It is fixed first, and
the honest-unmapped half of the fix ships before the mapping work behind it.

---

## Decisions

Three forks in the build prompt had real alternatives and are hard to reverse once data exists. They
were settled before any code was written.

### D1 — Interaction and allergy rules are keyed on a normalised INN name

`interaction_rule.subject_value` holds `ingredient.ingredient_key` — a normalised INN name such as
`warfarin` — when `subject_kind = 'Ingredient'`, and an ATC code when `subject_kind = 'AtcClass'`.

**Rejected: ingredient uuid.** Referentially stronger, but every seed row would need a name→id
resolution step and a pharmacist reviewing the table for correctness — which the governance rule in
Gate 11 requires them to do — would be reading uuids instead of molecules. A clinical rule nobody can
proofread is not a reviewed rule.

**Rejected: ATC-5 only.** It would remove the ingredient table entirely, and it fails on exactly the
products these checks exist for. 14.8% of the catalogue carries no ATC code at all, and a combination
product carries **one** ATC for the compound: co-amoxiclav is `J01CR02`, which cannot decompose into
amoxicillin and clavulanic acid. Gate 5's paracetamol-hidden-in-a-cold-remedy case — the classic
accidental overdose — is undetectable under an ATC-only key.

The key is text, so no foreign key can span the polymorphic `(subject_kind, subject_value)` pair.
Integrity is enforced instead at activation: a rule whose subject or object does not resolve against
`ingredient` or `atc_class` cannot be set `is_active = true`, and the loader reports it.

### D2 — Sequencing: the safety trio ships first

Gates 1, 2 and 4 — the allergy false negative, the trusted-client diagnosis hole, and severity tiering
— are the live-defect fixes, are independently shippable, and land as three commits with the full suite
green after each. The data-model gates (3, 5–11) follow after review.

### D3 — Pregnancy is its own table, not a column on `beneficiary_clinical`

`emr.beneficiary_clinical` is documented as holding "standing clinical facts that are not encounter
observations" — blood group, which does not change. Pregnancy is the opposite: a status with a shelf
life. It gets `emr.pregnancy_status` (one current row per beneficiary, `Pregnant | NotPregnant |
Unknown`, optional EDD, `recorded_by`/`recorded_at`) plus an append-only `pregnancy_status_history`.

`Unknown` is a real recorded value, not the absence of a row, and a status older than a configurable
window reads as stale rather than current — the same discipline Gate 8 applies to weight. A
fourteen-month-old "Pregnant" is not a current pregnancy.

---

## Shared foundation

Gates 1, 3 and 5 all need one thing before they need anything else: the ability to resolve a catalogue
product into the set of ingredients it actually contains.

### `libs/ingredients` — one normaliser, extracted not duplicated

`IngredientTokens` already exists inside `libs/clinical-validation/LabelEvidence.cs:74-230`. It
lower-cases, collapses whitespace, strips salt and hydrate suffixes **from the end only** (so "sodium
chloride" does not become "chloride" and match a disinfectant), maps INN↔USAN spellings, and splits
combinations on `+` and `/` while deliberately not splitting on commas. That is precisely the logic
allergen matching and duplicate-therapy detection need.

It is trapped in the wrong project. `Mersal.MasterData.Domain` must not depend on
`Mersal.ClinicalValidation` — reference data sits below the prescribing engine, not above it — and the
build prompt forbids writing a second normaliser, correctly: two implementations of a matching rule
diverge, and the divergence is silent.

So `IngredientTokens` moves wholesale into a new dependency-free project, `libs/ingredients` →
`Mersal.Ingredients`, referenced by both. `Mersal.ClinicalValidation` gains its first
`ProjectReference`; the csproj comment that currently forbids all of them is amended to say what it
actually means — the ban is on I/O, EF and HTTP, not on pure domain vocabulary. An architecture test
asserts there is exactly one `Normalize` implementation.

### `masterdata` — the resolution tables

Created in Gate 1's migration rather than Gate 3's, because allergy matching at ingredient level needs
them just as much as interactions do, and building them twice is how the two drift apart.

```sql
masterdata.ingredient
    ingredient_id    uuid PRIMARY KEY          -- derived from the key, stable across reloads
    ingredient_key   text NOT NULL UNIQUE      -- normalised INN: 'warfarin', 'amoxicillin'
    name_en, name_ar text NOT NULL
    atc_code         text NULL                 -- the substance-level ATC where one exists
    rxcui            text NULL                 -- reserved; nothing populates it yet
    is_active        boolean NOT NULL DEFAULT true

masterdata.drug_ingredient
    drug_id          uuid NOT NULL REFERENCES masterdata.drug
    ingredient_key   text NOT NULL REFERENCES masterdata.ingredient(ingredient_key)
    ordinal          int  NOT NULL             -- position within the product
    strength         text NULL
    PRIMARY KEY (drug_id, ingredient_key)
```

Populated from `drug.scientific_name` through `IngredientTokens.Candidates`. **Combination products
produce multiple rows — that is the entire point of the table.** `amoxicillin+clavulanic acid` becomes
two rows on the day the migration runs, which is what makes Gate 3's "screen both ingredients" and
Gate 5's "the paracetamol is inside the cold remedy" possible at all.

A product whose `scientific_name` is absent (4.7% of the catalogue) or is not an ingredient at all
("sun protection formula") produces **no rows**, and that absence is load-bearing: a drug with no
resolved ingredients is a drug the ingredient-level checks cannot evaluate, and they must say so rather
than pass it.

---

## Gate 1 — the allergy false negative

### 1.1 Make the unmapped case honest first

Shipped ahead of the mapping work, because a check that admits it did not run is safe and one that
claims a clean result is not.

`AllergyScreen` gains `UnmappedAllergens: IReadOnlyList<string>` — the display names of recorded
allergens that could not be resolved to any ingredient, ATC scope or cross-reactivity group.
`AllergyChecks.Evaluate` then loses its ability to say `Ok` without having evaluated:

| Situation | Today | After |
|---|---|---|
| source down | `Unavailable` | `Unavailable` — unchanged |
| no allergies recorded | `NotChecked` | `NotChecked` — unchanged |
| conflict found | `Warning` | `Warning`, severity-tiered by Gate 4 |
| **every recorded allergen unmapped** | **`Ok`** | `NotChecked` — *"'Penicillins' has no ingredient mapping, so it was not checked"* |
| **some mapped, none conflicting** | **`Ok`** | `NotChecked`, naming the unmapped ones |
| all mapped, none conflicting | `Ok` | `Ok` — *"checked against all 3 recorded allergies"* |

The partial case is `NotChecked` deliberately. "Checked two of your three allergies and found nothing"
rendered as a green tick is the same false assurance in a smaller dose, and invariant 1 admits no dose.

### 1.2 The mapping

```sql
masterdata.allergen_ingredient (allergen_id, ingredient_key)      -- exact ingredient matches
masterdata.allergen
  + atc_scopes             text[] NOT NULL DEFAULT '{}'           -- e.g. {'J01C'} for penicillins
  + cross_reactivity_group varchar(32) NULL
  + is_drug_mappable       boolean NOT NULL DEFAULT true
  + mapping_source         text NULL
  + mapping_reviewed_by    text NULL
  + mapping_reviewed_at    timestamptz NULL
```

All fifteen seeded allergens are mapped. The seven `Drug`-category ones resolve to ingredients, ATC
scopes or a cross-reactivity group; the eight `Food` and `Environmental` ones — peanut, egg, milk,
shellfish, gluten, pollen, dust, latex — carry `is_drug_mappable = false`, which makes them report as
**inapplicable to a medicine** rather than as unmapped. The distinction matters: an unmapped drug allergen is a gap in our
data, and a food allergen is simply not a question about a prescription. Conflating them would make
every patient with a dust-mite allergy look like a coverage failure.

Matching evaluates in precedence order — **exact ingredient → ATC scope → cross-reactivity group** —
and `matchedOn` carries which one fired, so the finding can say what it matched on rather than the
current uninformative `"atc-class"`.

Every mapping carries `mapping_source` and `mapping_reviewed_by`. An unreviewed mapping is not better
than no mapping: it produces confident findings from unattributable clinical judgement, which is the
thing doc 43 §1 rule 2 exists to prevent.

### 1.3 Cross-reactivity, with the evidence encoded

```sql
masterdata.cross_reactivity_group
    group_code    varchar(32) PRIMARY KEY
    name_en, name_ar     text NOT NULL
    confidence    text NOT NULL CHECK (confidence IN ('High','Moderate','Low','Theoretical'))
    statement_en, statement_ar  text NOT NULL
    citation      text NOT NULL
    source        text NOT NULL
    reviewed_by   text, reviewed_at timestamptz
```

Modelled **side-chain aware, not ring-based**. The historically quoted ~10% penicillin/cephalosporin
cross-reactivity figure is not supported by modern evidence; risk tracks R1 side-chain similarity rather
than the shared beta-lactam ring, and blanket cephalosporin avoidance after a penicillin label causes
real harm through inferior antibiotic choice. So:

- aminopenicillins (amoxicillin, ampicillin) ↔ cephalosporins sharing the aminobenzyl R1 side chain
  (cefalexin, cefaclor, cefadroxil, cefprozil) — **Moderate** confidence;
- penicillin ↔ cephalosporin generally, no shared side chain — **Low**, and the finding text says so.

A Low or Theoretical cross-reaction is a **Moderate** finding at most under Gate 4's tiering, so it
renders inline and does not interrupt.

**Sulfonamide-antibiotic → non-antibiotic sulfonamide is not encoded.** That association is not
supported by the evidence, and over-flagging it is a documented CDS defect.

### 1.4 The test that does not exist today

A penicillin-allergic beneficiary prescribed amoxicillin gets a `Warning` — asserted **through the real
masterdata matcher, the engine and the API**, not against a hand-built `AllergyScreen` fixture. The
current tests (`PrescriptionValidatorTests.cs:194-228`) construct the screen by hand and therefore never
exercise the matcher, which is exactly why a matcher incapable of matching shipped and stayed shipped.

A second test walks all fifteen seeded allergens and asserts none of them silently passes: each is
either mapped, or inapplicable, or reports `NotChecked` by name.

Registered in `docs/quality/invariant-registry.yaml` as `allergy-unmapped-is-NotChecked` and
`penicillin-allergy-warns`.

---

## Gate 2 — server-side diagnosis

`Prescriptions.cs:77` reads `req.DiagnosisIcdCodes`. Step 2 carefully re-runs every check server-side
and then takes its most important input from the client.

**The structural fix: diagnoses move out of `ValidationRequest` and into `ValidationSnapshot`.**

They are fetched data, not request data. Keeping them on the request is what made trusting the client
*expressible* — and phase 26 proved that an invariant carried by the type system survives, while one
carried by review does not.

```csharp
// ValidationRequest loses DiagnosisIcdCodes entirely.
public sealed record ValidationRequest(
    Guid EncounterId,
    IReadOnlyList<PrescriptionLineInput> Lines,
    IReadOnlyList<Guid> ActiveMedicationDrugIds);

// ValidationSnapshot gains them, with provenance, behind Fetched<T>.
public enum DiagnosisProvenance { EncounterFetched, ClientSupplied }
public sealed record DiagnosisContext(IReadOnlyList<string> IcdCodes, DiagnosisProvenance Source);
```

- **emr** gains `GET /api/v1/encounters/{id}/validation-context`, behind the same `ClinicalGate` as
  `/clinical`. Deliberately a new lean endpoint rather than a reuse of `/clinical`, which returns notes,
  vitals, allergies and medication history — minimum-necessary is a project rule, and this call is made
  on every keystroke-triggered validation. Gates 8 and 9 extend the same response with weight and
  pregnancy, so patient context costs one call and one audited read, not three.
- **Step 2** fetches server-side. `req.DiagnosisIcdCodes` is read nowhere on that path.
- **Step 1** may keep the client's list for speed, wrapped as `ClientSupplied`. The provenance is stored
  on the run, so a step-1/step-2 divergence is explainable rather than mysterious.
- **emr unreachable at step 2** → `Fetched.NotAvailable` → the indication and (Gate 9) contraindication
  checks report `Unavailable`. Distinct from "no diagnosis recorded", which stays `NotChecked`. Two
  different facts; two different states; neither is `Ok`.

**Test:** submit against an encounter carrying a contraindicated diagnosis with a forged or empty
`diagnosisIcdCodes` array, and assert the server still produces the finding. Extends
`ForgedClientVerdictTests`; registry-pinned as `server-fetches-diagnosis`.

---

## Gate 4 — severity tiering

Every clinical finding today is a `Warning` requiring a typed acknowledgement
(`Findings.cs:176`, `RequiresAcknowledgement => State is CheckState.Warning`). A contraindicated pair
and a trivial one demand the same click. Override rates above 90% are routinely reported in the CDS
literature and this is the mechanism: uniform alerting destroys the signal of the alerts that matter.

- `InteractionSeverity` becomes `ClinicalSeverity` — same four members, now carried by every check kind
  rather than only interactions.
- `RequiresAcknowledgement` becomes `State is Warning && Severity is null or Major or Contraindicated`.
  **A null severity stays interruptive.** Manufacturer-label interactions carry no grade because the
  label states an effect rather than a rank; downgrading them to inline would be this code inventing a
  clinical judgement it has no source for.
- `RequiresTypedReason` is new, and true only for `Contraindicated`. The override row records the
  severity so the approver sees what was overridden, not merely that something was.

| Severity | Interrupts | Gates submit | Renders |
|---|---|---|---|
| Contraindicated | yes | yes | modal, typed reason required, surfaced to approver |
| Major | yes | yes | modal, acknowledgeable with a reason |
| Moderate | no | **no** | inline on the line |
| Minor | no | no | collapsed by default |

**UI.** Severity becomes a first-class element with its own four-cue chip (hue, icon, shape, word), not
a word interpolated into a message string as at `PrescriptionValidator.cs:211-213`. The line chip shows
the worst severity beside the state chip; the modal groups by severity, then by kind. The existing
five-state model, the answered/unanswered visual classes and the Unavailable≠Ok distinction are
untouched — this gate changes **interruption**, never **blocking**.

**Tests:** a gating matrix per severity; chip rendering per severity; axe EN + AR; and an explicit
assertion that the five-state model has not regressed.

---

## Gates 3, 5–11 — after review

Summarised here so the spec is complete; each is designed in full when it is built.

| Gate | Shape |
|---|---|
| **3** Ingredient-level interactions | `interaction_rule` keyed per D1, unordered-pair unique, mechanism/effect/management bilingual, mandatory citation. Evaluation resolves products to ingredient sets, expands ATC ancestors, matches across the cartesian product, dedupes by rule. Seeded from the ONC/NLM high-priority list. Coverage stated in the UI. |
| **5** Duplicate therapy | New `CheckKind.Duplication`. Same ingredient across lines (including inside combinations) and same ATC-4 class. Major above a configured combined-dose ceiling, Moderate otherwise. Removes the `if (a.DrugId == b.DrugId) continue;` skip at `PrescriptionValidator.cs:168`. |
| **6** Mechanism / consequence / management | Structured fields on every finding class, not only interactions. The management line renders prominently, never inside a collapsed `<details>`. |
| **7** ICD hierarchy | `icd_code.parent_code` + `node_kind` + a closure table. **The source already carries this**: `Raw Files/ICD10_2019_full.csv` has `Parent_Code`, `Chapter_Code` and `Block_Code` columns, and `IcdCsvRow` binds four of its nine columns. Block ranges (`J00-J06`) expand to member categories at load time. Matching becomes descendant-or-self; the inverse case reports as "possible, less specific". The duplicate normaliser at `PrescriptionValidator.cs:106-110` is deleted, asserted by an architecture test. |
| **8** Age and weight | `ValidationRequest` gains patient context, fetched server-side through Gate 2's endpoint. Stale weight (>90d adult, >30d child) is not a current weight. Missing input ⇒ `NotChecked` naming it. Renal function is explicitly `Unknown` — lab results are `result_value text` and there is no structured eGFR to read. Age comes from patient-service, which pharmacy already has a client for; only the derived age crosses into the engine, never the date of birth. |
| **9** Drug–disease contraindications | `drug_disease_contraindication` keyed to Gate 7's hierarchy. Pregnancy per D3. Classic pairs seeded with citations. FDA letter categories are **not** modelled — they were replaced by narrative labelling in 2015, so the model carries a statement and its source. |
| **10** Indication-keyed dosing | `dosing_rule` on (subject, indication scope, population, route), most-specific-match selection, weight-based rules capped at the adult maximum. The recommended range displays beside the entered dose with its citation — which informs the override rather than merely obstructing it. |
| **11** Docs, registry, ADR | ADR-0028. Clinical governance enforced by constraint: no active clinical rule without a named pharmacist reviewer and a citation. |

---

## Invariants this phase adds

1. **No check returns `Ok` unless it actually evaluated.** An impossible match, a missing mapping or a
   missing patient input yields `NotChecked` **naming what was missing**.
2. Interactions, contraindications and duplication evaluate at **ingredient level**; combination
   products decompose.
3. Only **Contraindicated** and **Major** gate submission.
4. Every clinical finding carries **mechanism, consequence and management** where its source provides
   them, plus a citation.
5. The server reads the diagnosis **from the encounter**, never from the request body.
6. ICD matching is a **hierarchy walk**, in one implementation, with one dot-normalisation.

And every phase-26 invariant continues to hold: five states, `Fetched<T>`, no clinical check can block,
step 2 re-evaluates from scratch, four-cue chips, `Unavailable` ≠ `Ok`.
