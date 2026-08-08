# ADR-0033 — Live manufacturer-label checking against openFDA

**Status:** **Accepted** · **Date:** 2026-08-04 · **Phase:** 26 (amendment)
**Amends:** [ADR-0032](0032-prescribing-validation-and-local-interaction-checking.md) Decision 1 —
interaction checking is no longer *only* local. Decisions 2, 3 and 4 of that ADR are unchanged and this
design is built to satisfy them.
**Design:** [`HBMP-Design/43-approval-engine-and-prescribing-support.md`](../../HBMP-Design/43-approval-engine-and-prescribing-support.md)

---

## Context

ADR-0032 decided that interaction checking would be evaluated **locally**, against
`masterdata.drug_interaction`, and explicitly ruled out wiring an external interaction API. The reasoning was
sound and still is: NLM's RxNav interaction API was withdrawn in January 2024 and DrugBank's free tier
retires in March 2026, so a safety check resting on an unlicensed free endpoint is one that disappears
without notice.

What that ADR did not weigh is what happens if the local table is never populated. It has not been.
`masterdata.drug_interaction` holds **zero pairs**, which means the interaction check reports
`NotChecked` on every line of every prescription, and has done since the day it shipped. `masterdata` has no
dosing-rule table at all, so the dose check reports `NotChecked` too. Two of the five checks are, in
practice, decorative.

A check that is honest about knowing nothing is much better than one that pretends. It is still worth less
than one that knows something.

---

## Decision 1 — openFDA is added as a SECOND interaction source, not a replacement

The curated local list remains the system of record and keeps its own provenance. The label pass is
additive: each line may now carry two `Interaction` findings, from two sources of different authority, and
the UI attributes both rather than collapsing them. The local table is the one a pharmacist can correct, and
importing a licensed dataset into it stays the intended end state.

**What openFDA can actually support.** Its `drug_interactions` field is narrative label prose, not
severity-graded pairs — ADR-0032 was right about that. But the prose is matchable: warfarin's label names
amiodarone, fluconazole and ibuprofen, and does not name metformin. Scanning each drug's label for the other
drugs **on the same prescription**, in both directions, is a real signal.

**What it cannot support is a dose verdict**, and we do not manufacture one. See Decision 3.

---

## Decision 2 — The label scan may WARN but may never REASSURE

A label's interactions section is written per product, as prose, by the manufacturer. A mention is evidence.
A silence is not: an interaction can exist without appearing in the text.

So a clean scan reports `NotChecked`, never `Ok`. This is the asymmetry the whole feature turns on, and it is
the reason the check is safe to add at all.

**The honest cost, stated rather than discovered later:** because this pass can never return `Ok`, and
`NotChecked` outranks `Ok` in the per-line roll-up, a line on a multi-drug prescription can no longer
summarise as green however clean everything else is. That is a real reduction in the signal the summary
carries, accepted deliberately in exchange for the check existing.

It is bounded in one place: where a prescription has only one drug there is no pair to ask about, so the pass
emits **nothing at all** rather than a permanent "not checked". Inapplicable is not the same as skipped, and
parking every single-drug prescription in the unchecked state for ever would drain the meaning out of that
state on the prescriptions where something really was skipped.

---

## Decision 3 — Dose and duration are shown as REFERENCE, never graded

openFDA publishes dosing as prose — *"individualize dosing regimen for each patient, and adjust based on INR
response"* — and there is no structured maximum daily dose or treatment length anywhere in the dataset.

Where no curated dosing rule exists, the manufacturer's dosing section is displayed beside what the
prescriber typed, with the state left at `NotChecked` and the message saying in both languages that it has
**not** been compared. Extracting a ceiling from that prose would produce a number the platform cannot
defend, and it would fail hardest on the narrative-dosed drugs — warfarin, insulin, cytotoxics — where a
wrong ceiling does the most harm.

**Marketed strengths are included in the same reference block and deliberately not compared.** FDA strengths
are the strengths of *US* products; Egyptian presentations differ legitimately and often. A mismatch warning
would fire on correct prescriptions, and because a warning requires an acknowledgement reason before
submission, it would tax every one of them. A prescriber reading the list can spot a discrepancy that matters
far better than a rule that cannot tell a market difference from an error.

Where a curated rule *does* exist it still decides, and label text does not dilute it.

---

## Decision 4 — Only an ingredient name leaves the platform

openFDA is a public US government API and this is the only third-party call in the prescribing path.
`IDrugLabelSource` accepts a product id and an ingredient name and nothing else; `OpenFdaLabelSource` sends
only the ingredient. No beneficiary, no encounter, no prescription, and not the internal drug id — which is a
stable identifier that would otherwise accumulate into a profile of what a given clinic prescribes.

The interface is the guarantee rather than the discipline: it cannot express a beneficiary, so it cannot leak
one however it is later edited. ADR-0032's DPIA concern is therefore not triggered — nothing personal is
disclosed.

---

## Decision 5 — Matching is by active ingredient, and an inexact match is refused

Egyptian trade names do not resolve against a US dataset, so matching goes through
`masterdata.drug.scientific_name` (recorded for 28,865 of 31,651 products). Three hazards, all measured
against the real catalogue:

| Hazard | Example | Handling |
|---|---|---|
| Salt forms | `diclofenac sodium` | Strip salts from the **suffix only**. Stripping anywhere turns `sodium chloride` into `chloride`, which openFDA answers with *benzalkonium chloride* — a disinfectant. |
| INN vs USAN | `paracetamol`, `salbutamol`, `adrenaline` all 404 | An explicit synonym map, plus the catalogue's own `paracetamol(acetaminophen)` bracket form. |
| Near matches | `amoxicillin` returns the amox/clavulanate combination label first | Accept a result only when its generic name **equals** the ingredient after normalisation. Otherwise report not-checked. |

**The near miss is the dangerous case, not the miss.** A wrong label returns 200, a full interactions section
and complete confidence. "Not checked" is a far better answer than another molecule's. Coverage measured on
the 40 most-stocked ingredients is ~95%.

---

## Decision 6 — Failure is loud, and separated from absence

`LabelEvidence` carries two distinct buckets: `Unmatched` (there is no such label — an answer, renders
`NotChecked`) and `Failed` (timeout, 429, 5xx, unparseable — renders `Unavailable`). Merging them would let an
openFDA outage read exactly like a drug that has no US label, which is the precise failure mode `Fetched<T>`
exists to prevent.

This is also what makes ADR-0032's original objection survivable. openFDA may well disappear the way RxNav
did — but when it does, prescribers see "check unavailable", not silence.

**Rate limits and caching.** openFDA allows 1,000 requests/day per IP without a key. A five-line prescription
revalidated as a doctor types would exhaust a clinic's daily allowance before lunch. Labels are cached 24h on
success and 30 min on a miss; failures are never cached as answers, so a rate limit at 10am does not make a
drug uncheckable until 10am tomorrow. `OpenFda:ApiKey` raises the quota to 120,000/day and belongs in
secrets, never in the repository. `OpenFda:BaseUrl` allows an air-gapped deployment to point at a mirror.

---

## Consequences

- Interactions are checked for the first time in production, across the drugs on one prescription.
- No line on a multi-drug prescription summarises as `Ok` any more (Decision 2).
- The platform now has a runtime dependency on a third-party service in the prescribing path. It degrades to
  `Unavailable` and never to a pass, and it is one config value away from being pointed elsewhere.
- Populating `masterdata.drug_interaction` with a licensed dataset remains worth doing. This makes the gap
  survivable; it does not close it.
