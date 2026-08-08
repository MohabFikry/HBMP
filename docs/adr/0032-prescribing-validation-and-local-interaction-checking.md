# ADR-0032 — Prescribing validation: local interaction checking, and why clinical checks warn

**Status:** **Accepted** · **Date:** 2026-08-03 · **Phase:** 26
**Supersedes:** nothing · **Extends:** [ADR-0011](0011-tenant-rls.md) (RLS), and the three-state rendering
discipline recorded for the patient profile in [ADR-0026](0026-patient-profile-server-side-projection.md).
**Design:** [`HBMP-Design/43-approval-engine-and-prescribing-support.md`](../../HBMP-Design/43-approval-engine-and-prescribing-support.md) ·
Build prompt: `HBMP-Design/claude-code-prompts/phase-26-prescribing-workspace.md`

> **Numbering.** The build prompt asks for this as `ADR-0026`. That number was taken in phase 20 by the
> patient-profile projection ADR, and the sequence has since run to 0031. ADR numbers are one platform-wide
> sequence rather than per-phase; phases 21 and 25 hit the same collision and resolved it by taking the next
> free number (ADR-0028, ADR-0029). This is that resolution, recorded rather than silently applied: the phase
> is 26, the ADR is 0032.

---

## Context

Phase 26 puts automated advice in front of a doctor prescribing for refugee beneficiaries. The build prompt
assumed a free drug-interaction API and a drug list carrying indications per drug. Neither assumption
survived contact with reality, and the differences changed the design rather than the implementation.

---

## Decision 1 — Interaction checking is LOCAL, against a table Mersal owns

**The free interaction APIs are gone.** NLM's RxNav Drug Interaction API was withdrawn on 2 January 2024
with no replacement and no migration path. DrugBank retires its free interaction checker in March 2026.
openFDA is live and free, but serves US structured product labels: interactions appear as free-text prose in
a label section, not as severity-graded pairs, and there is no field that answers "is this dose right for
this diagnosis".

Two further mismatches are specific to Mersal. openFDA and RxNorm are US-centric, so Egyptian trade names do
not resolve — any mapping has to go through the active ingredient. And sending a beneficiary's diagnosis plus
medication list to a third party is a PHI disclosure to an external processor, governed by the platform's own
DPIA gate.

**So `masterdata.drug_interaction` — which already existed and was empty — is the system of record.**
Checking is evaluated locally, behind an adapter seam so a licensed dataset can be imported into the same
table later.

The reason is not cost. A safety check that depends on an unlicensed free endpoint is a safety check that
disappears without notice, which is exactly what happened twice in twenty-four months. Keeping it local also
keeps PHI inside the platform, so the core check needs no DPIA.

**The honest consequence:** internal curation means partial coverage. The UI says so — "checked against
Mersal's own interaction list (N pairs); coverage is partial" — rather than implying completeness. An empty
table reports **not checked**, never OK.

---

## Decision 2 — Clinical checks may WARN; only benefit rules may BLOCK

Benefit rules are deterministic statements about a policy Mersal itself authors, explainable line by line.
Clinical advice here is neither: the interaction list is partial by construction, and the indication mapping
is ATC-level clinical judgement rather than a published dataset (Decision 3). Blocking a prescriber on advice
of that provenance would be the greater harm.

Overrides are therefore expected and recorded, not prevented. A warning is passed by giving a reason, and the
reason is stored and shown to the approver. What is not available is proceeding *silently*.

**Enforced by the type system, not by review.** Every clinical checker returns `ClinicalState`, which has no
`Blocked` value; only `Finding.Benefit` can produce one. Writing a blocking clinical check is a compile
error.

---

## Decision 3 — "Check unavailable" is a different value from "no findings"

Before this phase, `pharmacy/Api/HttpClients.cs` caught every `HttpRequestException` and returned no alerts,
and treated every non-2xx response the same way through a bare `if (resp.IsSuccessStatusCode)` — six such
paths across three calls. An outage rendered to the prescriber as a clean bill of health.

Deleting the catches would have fixed the instances. What is recorded here fixes the class: ports return

```
Fetched<T> = Available(T, provenance) | Unavailable(reason)
```

`Unavailable` carries **no payload**. There is no empty collection for a checker to inspect and conclude
"nothing found", so there is no code path from a failed fetch to `Ok`.

The UI carries the same distinction structurally. Five states, four cues each, in **two visual classes**:
answered (`Ok`, `Warning`, `Blocked`) render as solid filled chips; unanswered (`NotChecked`,
`Unavailable`) render with a dashed border and a hollow glyph. A reader scanning a column sees "we have no
answer here" before parsing colour or text.

This is the same three-state discipline the patient profile records — Visible / Restricted / **Unavailable**.

---

## Decision 4 — openFDA is reference text only, mirrored, never live per-patient

If openFDA is used at all it is mirrored and cached, shown labelled as *"US FDA label — reference, not a
coverage or dosing decision"*. It is never parsed into a severity and never used to block. No PHI leaves the
platform, and there is no availability dependency in a clinical path.

**Nothing in phase 26 calls it.** The seam is described so the decision is on record before someone adds one.

---

## Decision 5 — Dose checking is scoped honestly

An automated "is this dose correct for this diagnosis" cannot be derived from label prose. What is defensible
is structured dosing rules for a curated, high-risk subset — maximum daily dose, duration ceiling, paediatric
weight band, renal flag — authored by the supervisor in phase 27's rule engine.

No such rules exist yet, so the dose check reports **"no dosing rule configured"** for every drug. It
deliberately reports `NotChecked` rather than `Unavailable`: `Unavailable` would claim a service exists and
failed, and none does. Silence, or an implied endorsement, would be worse than either.

---

## What inspecting the drug list changed

`Master Lists/egyptian-drug-list_5.xlsx` was inspected before the loader was written, and three findings
altered the design. Full column mapping: `tools/masterdata-loader/README.md`.

1. **Every indication code is a 3-character ICD-10 category.** All 874 of them; not one is 4-character or
   dotted. But `masterdata.icd_code` stores dotted codes and `emr.diagnosis` records the specific one, so
   comparing by equality would have reported "not a listed indication" on virtually every prescription. A
   warning that always fires is a warning clinicians learn to click through, which is worse than no warning.
   **Both sides normalise to the category before comparison.**

2. **`Z76` is a filler, not an indication** — and **1,019 products (4.5%) carry it as their only code**. They
   load with zero indications and report "not checked". Storing it would let a product with no clinical data
   render as checked.

3. **The mapping is generated at ATC level 4 and is clinical judgement.** The workbook's own notes say so:
   *"the ATC-to-ICD step itself is still clinical judgement, not a published dataset, because no free
   authoritative drug-to-indication mapping exists. Spot-validate a stratified sample against EDA leaflets or
   FDA/EMA labels before this gates live claims."* That sentence is carried into the UI as the finding's
   caveat, because it is precisely what a prescriber needs in order to weigh an off-label warning.

The file also **cannot** supply an Arabic trade name (no such column exists; the combobox falls back to
English), a UNHCR formulary (the column is a header with no data — phase 27 must author one as a
`benefit_list`), or any dosing data.

---

## Decision 6 — `masterdata:read`, reversing a recorded position

masterdata-service served its catalogue behind a bare `RequireAuthorization()`. `MasterDataAuthzTests`
argued that this was correct and warned that someone would eventually "harden" it and break every clinical
screen. Phase 26.1 made that change deliberately, and the test's rationale was rewritten rather than left
contradicting the code.

The old argument is still true as a statement about clinicians, and the grant is correspondingly broad —
every role that holds any scope. What the scope adds is not restriction: reference-data reach becomes a
stated, reviewable, revocable line in the role matrix instead of an unstated consequence of holding a token,
and a service or integration token must ask for the catalogue rather than receive it by default.

Two consequences surfaced in testing and are recorded because neither is obvious:

- **Scope-gating also imposes MFA**, because every service sets `Auth:ProtectedScopeRequiresMfa=true`. This
  is consistent rather than a regression — any session that can reach `emr:read` is already MFA-backed.
- **Services reach masterdata with the caller's token** (service accounts are forbidden platform-wide), so
  the broad grant covers them. A 403 here would previously have rendered as "no interactions found", which
  is why that case is now a named test.

---

## Decision 7 — Step 1 is advisory and untrusted

Validation at prescribing time runs in the doctor's browser and informs a human. On submission the server
re-evaluates from current state and reads **nothing** the client claims about the outcome. A submission
carrying a clean verdict for a drug the engine refuses is still refused; otherwise a crafted payload walks
past the entire engine.

A divergence between the two steps is normal — eligibility, coverage and lists all move between them — and is
shown plainly rather than treated as an error. Both runs are recorded, stamped `Step1` / `Step2`, so a later
reviewer can see what the prescriber was shown *and* what the server concluded.

---

## Consequences

- Interaction coverage is partial until a dataset is licensed (doc 43 D3), and the UI states the extent.
- 8,998 catalogue rows from the superseded CSV load carry no indication data. They stay reachable by id and
  code so historical prescriptions resolve, but the prescribing search serves the current market list only —
  a prescriber must not be offered two entries for one product where only one can be checked.
- A 5-second per-source timeout was added. The platform has no resilience layer at all, and an unbounded wait
  would leave a doctor on a spinner mid-consultation. A timeout is an answer here: `Unavailable`.
- Doc 43 D5 asks to reuse the call-centre ≥2-identifier rule for card-number lookup. That rule was
  deliberately deleted with the challenge screen it belonged to, so it is implemented afresh in
  `GET /beneficiaries/resolve`.

## Invariants registered

`INV-CHECK-UNAVAILABLE-IS-NEVER-OK` · `INV-CLINICAL-CHECKS-ONLY-WARN` · `INV-ADVISORY-CARRIES-PROVENANCE` ·
`INV-INDICATION-MATCHED-AT-CATEGORY-LEVEL` · `INV-STEP2-IGNORES-CLIENT-VERDICT` ·
`INV-DIAGNOSIS-SNAPSHOT-IS-IMMUTABLE` · `INV-CARD-NUMBER-IS-NOT-AN-AUTHENTICATOR` ·
`INV-PRESCRIBING-COMBOBOX-CARRIES-A-REAL-DRUG-ID`
