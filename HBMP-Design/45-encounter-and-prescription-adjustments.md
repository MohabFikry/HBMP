# 45 — Encounter, Service History & Chronic Prescribing Adjustments

> Back to [00-README-INDEX.md](00-README-INDEX.md) · Builds on [43](43-approval-engine-and-prescribing-support.md) · [44](44-clinical-validation-hardening.md)
> Build prompt: [claude-code-prompts/phase-29-encounter-and-chronic-prescribing.md](claude-code-prompts/phase-29-encounter-and-chronic-prescribing.md)

Seven adjustments across the encounter, the drug master and the prescription model. Two are cosmetic, five are structural. The chronic-prescription model (§5) is the one that touches money, benefit limits and dispensing, and its four open questions are now decided.

---

## 1. "Radiology" replaces "Imaging" — everywhere, identifiers included

Not a label change. The role, the scopes, the enum values, the event names, the portal base, the routes and the UI strings all become **Radiology** (AR: **الأشعة**).

| Kind | From | To |
|---|---|---|
| Role | `imaging_tech` | `radiology_tech` |
| Scopes | `imaging:*` | `radiology:*` |
| Provider type | `provider_type = 'Imaging'` | `'Radiology'` |
| Order type | imaging order values | radiology values |
| Events | `Imaging*` names | `Radiology*` names |
| Portal base | `/imaging` | `/radiology` |
| UI strings | Imaging / تصوير | Radiology / الأشعة |

### Three things make this non-atomic

A rename across a running platform is not a find-and-replace, because three artefacts outlive the deployment that renames them:

1. **Unexpired access tokens** carry `imaging:*` scopes. A user signed in at the moment of deploy holds a token that is valid for the rest of its TTL and names a scope that no longer exists. Services must **accept both spellings** until the longest possible token has expired.
2. **In-flight outbox events** carry the old names. The outbox is durable by design, so events enqueued before the switch will be relayed after it. Consumers must **accept both names** until the outbox has drained. Renaming a published event without this is precisely the silent-failure class the phase-24 event-symmetry gate exists to catch.
3. **The audit chain is immutable and hash-linked.** Historical `audit_event` rows say "imaging" and **must not be rewritten** — rewriting them breaks the hash chain, which is the one property that makes the audit trail evidence. Old audit rows keep their old names forever; readers resolve them through a display alias. This is a permanent alias, not a migration step.

### Sequence

**Expand → backfill → switch → contract**, with the dual-accept window sized to *longer than the maximum access-token TTL* and *longer than the outbox drain time*:

1. **Expand** — add the new scope, the new CHECK value, the new role; services accept either scope; consumers accept either event name. Nothing is removed.
2. **Backfill** — migrate `provider_type` rows, role assignments, scope grants, branch-scoped role lists, the SPA scope list.
3. **Switch** — the issuer emits only new scopes; publishers emit only new event names; the UI uses the new identifiers.
4. **Contract** — after the window, drop the old scope, the old CHECK value and the old event acceptance.

**The frozen token contract is amended, not broken.** `docs/security/token-contract.md` is a deliberate contract with a byte-compat test; a scope rename changes it. That requires an ADR, an updated snapshot and updated fixtures — and the contract test must stay green against *both* the old and new fixtures during the dual-accept window, exactly as phase 21 handled additive claims.

## 2. OP Procedures — a fourth order type, not a fourth ordering system

A new **OP Procedures** tab in the encounter, beside Prescription / Labs / Radiology.

**It reuses `orders-service`.** An outpatient procedure is an order: it consumes benefit, may require pre-authorisation, is fulfilled by a provider, and is claimed. Building a parallel mechanism would fork the consume/authorise/claim path that took several phases to get right. New `order_type = 'Procedure'`, everything else inherited.

### Which CPT codes — all remaining categories

CPT Category I ranges divide as:

| Range | Section | Routes to |
|---|---|---|
| 10004–69990 | Surgery | **Procedure** order |
| 70010–79999 | Radiology | Radiology tab (existing) |
| 80047–89398 | Pathology & Laboratory | Labs tab (existing) |
| 90281–99607 | Medicine (injections, infusions, dialysis, **physiotherapy**, minor procedures) | **Procedure** order |
| 99202–99499 | Evaluation & Management | **Referral** — see below |

**Every remaining category is orderable.** Nothing is excluded.

**E/M becomes a referral, not a procedure.** When a doctor sends a beneficiary to an outside specialist for an evaluation, that *is* a referral — and the platform already has a referral entity, a `referral:write` scope, a referral state machine with loop-closure, and a `ReferralAdapter` in interop. An E/M code ordered for external fulfilment therefore creates a **Referral**, carrying the CPT code as its requested service, rather than a second mechanism that does the same thing under a different name.

So the encounter's OP Procedures tab produces one of two things depending on the code selected: a **Procedure order** (Surgery, Medicine) or a **Referral** (E/M). The doctor picks a service; the system decides the vehicle. That distinction matters downstream — a referral needs a loop closed with a report back, a procedure needs fulfilment and consumption.

### Procedure type, and session-based delivery

The composer carries a **type combobox** — Physiotherapy, Minor Surgery, Injection/Infusion, Dialysis, Wound Care, Rehabilitation, Diagnostic Procedure, Other.

**Type is master data, not an enum**, administered like refill frequency: adding "Hydrotherapy" must be a data change, not a release.

**Sessions are a property of the type, not a hard-coded exception for physiotherapy.** `procedure_type.is_session_based` drives the UI: selecting a session-based type reveals a **number of sessions** field; selecting any other type does not. Physiotherapy is session-based today; dialysis and rehabilitation obviously are too, and making the behaviour follow a flag means the second one costs nothing. Hard-coding `if (type === 'Physiotherapy')` would guarantee that conversation twice more.

**Sessions are the order line's quantity.** Not a parallel counter — the existing invariant already says consume is atomic and idempotent and *partial fulfilment leaves the remainder active*. Ten sessions is quantity 10, consumed one at a time, with the same concurrency proofs that protect every other consume path.

**Type must agree with the code.** Each type carries the CPT categories or ranges it may accompany; a physiotherapy type on a minor-surgery code is a data error and is refused with a clear message. Left unvalidated the field becomes decorative, and any reporting built on it is quietly wrong.

**Sessions authorised ≠ sessions requested.** If the doctor asks for ten and the approval team partially approves six, the deliverable count is **six**. The session count must flow from the *approved* scope, never the requested one — the platform already models partial approval, and this is the place it is easiest to get backwards.

`masterdata.cpt_code` already carries a `category` column populated by the loader. The routing map must be built from the **loaded values** and reconciled against the ranges above; where they disagree the range wins and the discrepancy is reported rather than silently resolved.

## 2b. External Provider Portal — where the procedure is actually delivered

Procedures and external referrals are fulfilled **outside** the six Mersal clinics: physiotherapy centres, specialist clinics, day-care units. Those organisations need a portal to see the order, confirm the beneficiary, deliver the service and report back — the same relationship labs and pharmacies already have, extended to a new provider kind.

### The mistake this must not repeat

Audit R3 found that `DispensingGate` checks only that the caller holds *a* `ProviderId` — never that the prescription belongs to *their* pharmacy. Any authenticated pharmacist with `pharmacy:read` browses the entire network queue. The class documentation says so explicitly.

**The procedure portal binds rows to the owning provider from the first commit.** `ProviderScoped` reach already exists in `libs/authz`; a centre sees the orders routed to it and nothing else. Retrofitting ownership onto a live queue is far harder than building it in, and this is the third portal where the same decision arises.

### What the centre sees — and does not

An external centre is not a Mersal clinician. Minimum-necessary applies with a narrower projection than any internal role:

| Sees | Never sees |
|---|---|
| Beneficiary identity sufficient to verify the person present, and the photo where consent allows | The EMR, notes, other encounters |
| The ordered service, its CPT code, quantity/sessions authorised | Diagnoses beyond the **referral reason the ordering doctor chose to share** |
| Authorisation status and validity | Coverage amounts, cost-share, claim values |
| Its own delivery history for this beneficiary | Anything from another provider |

The referral reason is the important nuance: a physiotherapist genuinely needs to know *why* they are treating someone, so the ordering doctor selects what clinical context travels with the referral. That is a deliberate disclosure by a clinician, not a blanket grant — and it is auditable as such.

### Multi-session delivery

Physiotherapy is not one visit. An order carries a **session count**, and the centre consumes it session by session — which is exactly the platform's existing partial-fulfilment invariant: *consume is atomic and idempotent; partial fulfilment leaves the remainder active*. No new mechanism, and the same concurrency proofs apply.

Each session records attendance, the practitioner who delivered it, and optionally a note. Completion closes the loop: a report back to the ordering doctor, which for a **referral** is mandatory — an open referral loop is the classic patient-safety failure in outpatient care, and the state machine already models closure.

### Identity at the counter

The centre verifies the person in front of them. That reuses the card-number path from phase 26 Gate 6 — a **second identifier required**, minimum-necessary view, audited retrieval. A card is shared and photographed; it is not an authenticator.

## 3. OP Procedures in the History tab

The History tab gains an OP Procedures section alongside the existing ones. Same projection rules, same sensitivity gating, no new access path.

## 4. Per-line service history — one component, one endpoint, no new bypass

Every service line — prescription, lab, radiology, OP procedure, and every history tab — gains an **icon opening a modal showing that individual service's full history for this patient**: every previous occurrence with date, actor, branch, status and outcome.

Clinically this is the highest-value item in the list. "Has this patient had this test before, and what did it show?" is the question that prevents duplicate ordering and reveals trends, and it is currently unanswerable without leaving the encounter.

**It must not become a side door.** The rules:

- **One server endpoint**, one shared component. Not one implementation per tab.
- Composed **server-side under the caller's token** and projected to the caller's role — the same rule as the patient profile ([39](39-patient-profile.md) §1). A withheld field is absent from the JSON.
- **The sensitivity gate still binds.** A restricted result ([37](37-branch-scoping-and-clinical-sensitivity.md) §6) appears as existence-only — date, service, actor, "restricted" — with the request-access action. A history modal that reveals a mental-health result the results inbox withholds would defeat the entire gate.
- Every open is an **audited PHI read** naming the service and the patient.
- Three states, distinctly: has history · **no previous occurrences** · **could not load**. The last must never render as "none".
- Where results are numeric and permitted, show the **trend** — that is the point of the feature.

## 5. Acute and chronic prescriptions

A toggle on the prescription: **Acute** (today's behaviour, unchanged) or **Chronic**.

### Rules

- **Chronic requires a duration greater than one month.** A 14-day course is not chronic; reject with a clear message rather than silently accepting.
- Chronic reveals a **refill frequency** combobox — Monthly / Every 2 months / Every 3 months — **configurable by the Approval Supervisor**, so it is a master table, not an enum. Adding "every 6 months" must be a data change, not a release.
- The script's validity spans the **whole duration**; it is dispensable in windows.

### Quantity calculation and splitting

Worked example — 90 days, monthly frequency, 1 tablet three times daily:

1. **Total** = dose × times/day × duration = 1 × 3 × 90 = **270 tablets**
2. **Round the total** to the dispensable unit for the form (§6) — tablets are splittable from a pack, so 270 stands
3. **Windows** = ⌈duration ÷ (frequency months × 30)⌉ = 3
4. **Allocate** across windows by largest-remainder, integer, **highest first**

Where the division is uneven — 100 tablets over 3 windows — the allocation is **34 / 33 / 33**. Highest first, as required.

**The allocation must sum exactly to the total.** Rounding each window independently lets the sum drift above the prescribed amount, which over-supplies the patient and over-consumes the benefit. So: round once at the total, then allocate integers whose sum is that total. Never round per window.

For non-splittable forms (inhalers, pre-filled pens, vials, sprays) the unit is the whole item, the total rounds **up** to a whole item, and allocation proceeds in whole items.

### The four decisions (now settled)

| Question | Decision | Consequence |
|---|---|---|
| **Authorisation** | **Once for the whole script**, but **eligibility re-validated at each dispense** | The approval team touches a stable chronic patient once. A member whose policy lapses in month 2 is stopped at the pharmacy — the script is *blocked*, not cancelled, and resumes if eligibility is restored |
| **Benefit limits** | **Consumed per dispense, as collected** | Limits reflect what the patient actually received. An uncollected month is never charged. Requires the consume path to handle partial release against one authorisation |
| **Rounding** | **Sub-unit where the form allows splitting a pack**; whole units otherwise | Driven by the form on the master sheet, not hard-coded per drug |
| **Refill windows** | **Fixed windows with an early tolerance** (configurable, default 5 days). **A missed window is forfeited.** Script expires at the end of the duration | Prevents stockpiling and prevents a patient collecting 60 days at once, which would defeat the split |

### Window model

Each window carries: number, scheduled open date, actual open date (scheduled − tolerance), close date, allocated quantity, dispensed quantity, and status — `Pending` · `Open` · `Dispensed` · `PartiallyDispensed` · `Missed` · `Blocked`.

- **Missed** is set by a sweeper when a window closes undispensed. The quantity is forfeited and cannot be claimed later.
- **Blocked** is set when eligibility fails at the pharmacy — distinct from Missed, because it is not the patient's doing and it must be visible to the case team.
- A window cannot be dispensed before it opens; attempting it is a clear refusal naming the open date, not a generic error.

## 6. Prescribing unit and pack size on the drug master

The drug master needs three facts it does not have:

| Field | Meaning | Examples |
|---|---|---|
| `prescribing_unit` | The unit a doctor prescribes in | Tablet, Capsule, mL, Puff, Spray, **IU**, Drop, Sachet, Suppository, Vial, Ampoule, Patch, Gram |
| `pack_size` | How many prescribing units are in one pack | 20 tablets · 200 puffs · 300 IU (a 3 mL 100 IU/mL pen) · 120 mL |
| `is_pack_splittable` | Whether a pack can be broken | Tablets/capsules yes; inhalers, pens, vials no |

Quantity then flows: `dose × times/day × duration = total prescribing units`, converted to packs where the pack cannot be split. Insulin prescribed in **IU** resolves to whole pens; a spray prescribed in **puffs** resolves to whole canisters.

`is_pack_splittable` defaults from the dosage form but must be **overridable per product** — the form is a good heuristic and a poor law.

`egyptian-drug-list_5.xlsx` reportedly carries pack size and unit. Those columns must be **inspected and mapped before the loader is written**; if a field is absent, the drug is marked `unit_data_incomplete` and quantity calculation reports **NotChecked naming the missing field** — never a guessed quantity. A silently wrong quantity is a dispensing error.

## 7. Lowest-price label and availability

### Equivalence grouping — the correction that matters

"Same active ingredients" alone is not a valid comparison group. A 500 mg tablet and a 250 mg/5 mL syrup share an ingredient and cannot be price-compared. The group must be **active ingredient + strength + dosage form/route**.

### Compare price per unit, not pack price

A 20-tablet pack at 100 EGP is **more** expensive per tablet than a 30-tablet pack at 120 EGP, yet pack price alone labels the first as cheaper. The comparison must be `price_egp ÷ pack_size` — **price per prescribing unit**. Labelling by pack price would actively mislead a prescriber trying to save a beneficiary money, which is the opposite of the feature's purpose.

Ties all receive the label. The flag is **derived, not authored** — recomputed whenever prices load, with a `computed_at` so a stale label is detectable.

### Availability

A new `availability` field, filled later. **It must not default to "unavailable."** Three states: `Available` · `Unavailable` · **`Unknown` (default)**. A boolean defaulting to false would render the entire catalogue as out of stock on day one, and prescribers would learn to ignore the indicator before it ever carried real data.

Unknown renders as no indicator at all — not as a warning. Only a positive `Unavailable` shows a badge, with the alternatives list already available from the ATC-alternatives endpoint.

## 8. Invariants

1. **The Radiology rename is complete** — role, scopes, enums, events, routes and strings. It proceeds expand → backfill → switch → contract, with a dual-accept window longer than the token TTL and the outbox drain. **Historical audit rows are never rewritten.**
2. **OP Procedures reuse `orders-service`** — no parallel ordering, authorisation or claim path.
3. **E/M codes create a Referral, not a Procedure.** Every other remaining CPT category is orderable.
3b. **An external provider sees only its own rows** — provider ownership is bound at the row level from the first commit, never "holds a ProviderId".
3c. **Clinical context travels only by the ordering doctor's explicit choice**, and that choice is audited.
4. Service history is **one endpoint and one component**, server-projected, sensitivity-gated, audited, three-state.
5. **A chronic allocation sums exactly to the prescribed total** — round once at the total, never per window.
6. **A missed window is forfeited**; a blocked window is not the patient's fault and is visible.
7. **Limits are consumed per dispense**, and eligibility is re-validated at each dispense against a single authorisation.
8. **Missing unit data yields NotChecked, never a guessed quantity.**
9. **Price comparison is per prescribing unit within ingredient + strength + form.**
10. **Availability defaults to Unknown**, and Unknown shows nothing.

## 9. Acceptance criteria

- [ ] Radiology rename complete across role, scopes, enums, events, routes and strings; a token minted before the switch still works during the dual-accept window; an outbox event enqueued before the switch is still consumed; the audit chain verifies unbroken and old rows still read "imaging" behind a display alias; the token-contract test is green against both fixtures.
- [ ] OP Procedures tab routes Surgery/Medicine to a `Procedure` order and E/M to a **Referral**, both through existing machinery; the CPT routing map is built from loaded values and reconciled against the published ranges with a report.
- [ ] External Provider Portal: a centre sees **only** orders routed to it (proved by a test with two providers); sessions consume partially and idempotently; a referral loop cannot close without a report back; identity verification requires a second identifier and is audited; the payload carries no diagnosis beyond what the ordering doctor chose to share.
- [ ] History tab carries OP Procedures with the same gating as its siblings.
- [ ] Every service line in every tab opens the same history modal; a restricted result shows existence-only with a request-access action; every open is audited; "could not load" is distinct from "no history".
- [ ] Acute/Chronic toggle; chronic rejects durations ≤ 1 month; frequency is a supervisor-configurable master table.
- [ ] 90 days monthly → 3 windows; 100 units over 3 windows → 34/33/33; allocation sums exactly to the total; non-splittable forms allocate whole units.
- [ ] One authorisation; eligibility re-validated per dispense; a lapsed member blocks the window without cancelling the script; limits consumed per dispense only.
- [ ] Early tolerance honoured; a window cannot be dispensed before it opens; a missed window is forfeited and cannot be claimed later.
- [ ] `prescribing_unit`, `pack_size`, `is_pack_splittable` loaded from the sheet with a mapping report; missing data yields NotChecked, never a guess.
- [ ] Lowest-price label computed per prescribing unit within ingredient + strength + form, ties included, with `computed_at`.
- [ ] `availability` defaults to Unknown and renders nothing; only Unavailable shows a badge.

---

### Cross-references
Prescribing engine: [43](43-approval-engine-and-prescribing-support.md) · Clinical checks: [44](44-clinical-validation-hardening.md) · Sensitivity gate: [37](37-branch-scoping-and-clinical-sensitivity.md) · Server projection: [39](39-patient-profile.md) · Build: [phase-29](claude-code-prompts/phase-29-encounter-and-chronic-prescribing.md)
