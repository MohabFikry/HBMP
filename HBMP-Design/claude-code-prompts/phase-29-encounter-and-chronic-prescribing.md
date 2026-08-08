# Phase 29 — Encounter tabs, service history & chronic prescribing

**Goal:** Seven adjustments — Radiology naming, an OP Procedures order type, per-line service history, the acute/chronic prescription model with refill windows, prescribing units and pack sizes on the drug master, and lowest-price / availability labelling.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Design: [`../45-encounter-and-prescription-adjustments.md`](../45-encounter-and-prescription-adjustments.md)

> **Two things carry real risk here.** The **chronic allocation** touches quantities, benefit limits and dispensing — an off-by-one over-supplies a patient and over-consumes their benefit. The **service-history modal** aggregates a patient's whole history of one service onto one screen, which is exactly the shape that becomes a bypass of the sensitivity gate if built naively.
>
> **The four chronic decisions are settled — do not re-open them:** one authorisation for the whole script with **eligibility re-validated at each dispense**; limits **consumed per dispense as collected**; rounding to the **sub-unit where the form allows splitting a pack**, whole units otherwise; **fixed windows with an early tolerance, a missed window forfeited**.

## Skills to activate
> **Superpowers:** **brainstorming** before Gate 5 (the window/allocation model has real alternatives and is hard to change once dispensing data exists); **writing-plans** for the migration order across masterdata / pharmacy / orders; **test-driven-development** for the allocation maths — it is pure arithmetic with exact expected values and belongs in Domain, written test-first.
> **Project skills:** `mersal-platform-architect`, `refugee-healthcare-management` (always-on), `pbm-adjudication-engine`, `clinical-workflow-designer`, `healthcare-database-architect`, `healthcare-uiux-designer`, `appointment-queue-management`.

## Context — read first
- [`../45-encounter-and-prescription-adjustments.md`](../45-encounter-and-prescription-adjustments.md) — **AUTHORITATIVE**, especially §5 (chronic), §6 (units), §7 (price/availability), §8 (invariants).
- [`../37-branch-scoping-and-clinical-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md) §6 (the gate the history modal must not weaken) · [`../39-patient-profile.md`](../39-patient-profile.md) §1 (server-side projection) · [`../43`](../43-approval-engine-and-prescribing-support.md), [`../44`](../44-clinical-validation-hardening.md).
- **Existing code:** `services/orders/**` (order types, consume, authorisation routing), `services/pharmacy/{Api/Prescriptions.cs,Api/Dispensing.cs,Domain/*,Infrastructure/Migrations/*}`, `services/masterdata/{Api/Program.cs,Infrastructure/Migrations/*}`, `tools/masterdata-loader/{Mappers.cs,Loaders.cs,DbUpsert.cs}`, `apps/web/src/screens/DoctorEncounter.tsx`, `apps/web/src/screens/prescribing/**`, `apps/web/src/portals/catalog.ts`, `libs/clinical-validation/**`.
- **Run DB-gated tests with `./dotnet.sh test --with-db`.**

## INVARIANTS (../45 §8)
1. Radiology is a **label**; no identifier, scope, role or enum is renamed.
2. OP Procedures **reuse `orders-service`**; no parallel ordering/auth/claim path.
3. **E/M codes are never orderable.**
4. Service history: one endpoint, one component, server-projected, sensitivity-gated, audited, three-state.
5. **A chronic allocation sums exactly to the prescribed total** — round once at the total, never per window.
6. A **missed** window is forfeited; a **blocked** window is distinct and visible.
7. Limits consumed **per dispense**; eligibility re-validated per dispense against **one** authorisation.
8. **Missing unit data ⇒ NotChecked, never a guessed quantity.**
9. Price comparison is **per prescribing unit** within ingredient + strength + form.
10. `availability` defaults to **Unknown**, which renders nothing.

---

## Gate 1 — Radiology rename, identifiers included (expand → backfill → switch → contract)

```text
Read ../45 §1. This is a FULL rename, not a label change: role `imaging_tech` -> `radiology_tech`,
scopes `imaging:*` -> `radiology:*`, provider_type 'Imaging' -> 'Radiology', order-type values, event
names, portal base /imaging -> /radiology, Kong routes, SPA scope list, and every UI string
(AR: الأشعة).

THREE ARTEFACTS OUTLIVE THE DEPLOY THAT RENAMES THEM. Handle each explicitly:
 (a) UNEXPIRED ACCESS TOKENS carry imaging:* scopes. Services must accept BOTH spellings until the
     longest possible token has expired.
 (b) IN-FLIGHT OUTBOX EVENTS carry old names. The outbox is durable, so events enqueued before the
     switch relay after it. Consumers must accept BOTH names until the outbox has drained. Renaming a
     published event without this is exactly the silent-failure class the phase-24 event-symmetry gate
     exists to catch — run that gate before and after.
 (c) THE AUDIT CHAIN IS HASH-LINKED AND IMMUTABLE. Historical audit_event rows say "imaging" and MUST
     NOT BE REWRITTEN — rewriting breaks the chain, which is the property that makes the audit trail
     evidence. Old rows keep old names FOREVER; add a permanent display alias for readers. Verify the
     chain still validates after the migration.

SEQUENCE (four commits, in order):
1. EXPAND — add the new scope, new CHECK value, new role; services accept either scope; consumers
   accept either event name. Remove nothing.
2. BACKFILL — migrate provider_type rows, role assignments, scope grants, BranchScopedRoles,
   apps/web permissions.ts Role union, the SPA scope list.
3. SWITCH — issuer emits only new scopes; publishers emit only new event names; UI uses new identifiers.
4. CONTRACT — after a window LONGER than the max access-token TTL AND longer than the outbox drain,
   drop the old scope, old CHECK value and old event acceptance.

FROZEN TOKEN CONTRACT: docs/security/token-contract.md is a deliberate contract with a byte-compat
test. A scope rename AMENDS it — write the ADR, update the snapshot, and keep the contract test GREEN
AGAINST BOTH old and new fixtures for the duration of the dual-accept window (the same way phase 21
handled additive claims).
ACCEPTANCE: a token minted before the switch still authorises during the window; an event enqueued
before the switch is still consumed; the audit chain verifies unbroken; old audit rows still read
"imaging" behind the alias; no user-facing string says Imaging; event-symmetry gate green.
```

## Gate 2 — OP Procedures: all remaining CPT, with E/M routed to Referral

```text
Read ../45 §2. An outpatient procedure IS an order; an external evaluation IS a referral. REUSE both
existing mechanisms — services/orders and the existing referral entity/state machine/`referral:write`
scope. Do NOT build a third thing.

- orders: add order_type = 'Procedure' (expand/contract on the CHECK). Consume atomicity, idempotency,
  authorisation routing and branch scope are inherited unchanged.
- ROUTING MAP — every remaining CPT category is orderable, nothing excluded:
    Surgery   10004-69990  -> Procedure order
    Medicine  90281-99607  -> Procedure order   (includes physiotherapy, injections, infusions)
    E/M       99202-99499  -> REFERRAL, carrying the CPT code as the requested service
    (Radiology 70010-79999 and Pathology/Lab 80047-89398 keep their existing tabs.)
  The doctor picks a service; the SYSTEM decides the vehicle. This matters downstream: a referral needs
  its loop closed with a report back; a procedure needs fulfilment and consumption.
- cpt_code.category is already loader-populated — BUILD THE MAP FROM LOADED VALUES and RECONCILE against
  the ranges above. Where they disagree the RANGE WINS and the discrepancy is REPORTED, not silently
  resolved. Emit the reconciliation as loader/migration output.
- Expose GET /api/v1/orderable-services?q=&kind= returning the code plus the vehicle it will create, so
  the UI can show the doctor what will happen before they commit.

PROCEDURE TYPE + SESSIONS (../45 §2 "Procedure type, and session-based delivery"):
- masterdata.procedure_type(code, name_en, name_ar, is_session_based bool, default_sessions int NULL,
  max_sessions int NULL, allowed_cpt_scopes jsonb, is_active, sort_order) — MASTER DATA, administered
  like refill_frequency. Seed: Physiotherapy(session-based), MinorSurgery, InjectionInfusion,
  Dialysis(session-based), WoundCare, Rehabilitation(session-based), DiagnosticProcedure, Other.
  Adding "Hydrotherapy" must be a DATA change, not a release.
- SESSIONS FOLLOW THE FLAG, NOT THE NAME. The composer shows a "number of sessions" field when the
  selected type has is_session_based = true. DO NOT write `if (type === 'Physiotherapy')` — dialysis and
  rehabilitation are session-based too, and hard-coding guarantees this conversation twice more.
- SESSIONS ARE THE ORDER LINE QUANTITY. Not a parallel counter. Ten sessions = quantity 10, consumed one
  at a time by the existing atomic/idempotent consume with the remainder staying active. Reuse it.
- VALIDATE TYPE AGAINST CODE: each type's allowed_cpt_scopes constrains which CPT codes it may
  accompany; a Physiotherapy type on a minor-surgery code is refused 422 with a clear message. An
  unvalidated type field is decorative and makes every report built on it quietly wrong.
- SESSIONS AUTHORISED != SESSIONS REQUESTED: if the doctor requests 10 and approvals partially approve 6,
  the deliverable count is 6. The session count MUST flow from the APPROVED scope, never the requested
  one. The platform already models partial approval — add the test, this is the easiest thing here to
  get backwards.
- Order carries a validity; sessions undelivered at expiry are FORFEITED and the order closes as
  partially fulfilled rather than lingering open. Consistent with the chronic-window decision.
ACCEPTANCE: a Surgery code creates a Procedure order through the SAME consume/authorise/claim path as a
lab order; an E/M code creates a Referral with loop-closure required; selecting a session-based type
reveals the sessions field and a non-session type does not; a type/code mismatch is refused; a partial
approval of 10→6 yields 6 deliverable sessions; the reconciliation report exists.
```

## Gate 2b — External Provider Portal (physiotherapy centres and outside clinics)

```text
Read ../45 §2b. Procedures and external referrals are delivered OUTSIDE the six Mersal clinics. Those
organisations need the same kind of portal labs and pharmacies already have.

DO NOT REPEAT THE DISPENSING-GATE DEFECT. services/pharmacy/Api/DispensingGate.cs:8-11 checks only that
the caller holds *a* ProviderId — never that the prescription belongs to THEIR pharmacy, so any
pharmacist with pharmacy:read browses the whole network queue. BIND ROWS TO THE OWNING PROVIDER FROM
THE FIRST COMMIT. ProviderScoped reach already exists in libs/authz — use it. Add the two-provider test
(provider A cannot see provider B's orders) before the queue endpoint exists.

- provider_type gains 'Physiotherapy' (and any other external kinds needed); the existing 'Clinic'
  covers generic outside clinics.
- New role `procedure_provider` + scopes `procedure:read`, `procedure:consume`, following the lab and
  pharmacy pattern exactly — one portal, ProviderScoped, no branch scope (external providers are not
  Mersal branches).
- MIN-NECESSARY PROJECTION, narrower than any internal role. The centre sees: beneficiary identity
  sufficient to verify the person present (+ photo where consent allows), the ordered service and CPT
  code, sessions authorised, authorisation status and validity, and ITS OWN delivery history for this
  beneficiary. It NEVER sees: the EMR, notes, other encounters, other providers' rows, coverage amounts,
  cost-share or claim values. Prove with a projection test over the SERIALIZED payload.
- CLINICAL CONTEXT IS AN EXPLICIT DISCLOSURE: the ordering doctor CHOOSES what referral reason / context
  travels with the order. A physiotherapist genuinely needs to know why they are treating someone, but
  that is a clinician's deliberate disclosure, not a blanket grant. Record the choice; audit it.
- MULTI-SESSION DELIVERY, ONE AT A TIME: the order arrives with N APPROVED sessions and the centre
  consumes them one by one as delivered — the EXISTING partial-fulfilment invariant (atomic, idempotent,
  remainder stays active). Reuse the consume path and its concurrency proofs; do not write a second one.
  Each consume records date, delivering practitioner, attendance and an optional note, and carries its
  OWN IDEMPOTENCY KEY PER SESSION — a double-tapped "record session" must not burn two of a
  beneficiary's six approved visits. Add the replay test.
  Show progress plainly ("4 of 6 sessions delivered") in BOTH the centre's queue and the ordering
  doctor's worklist. Benefit is consumed PER SESSION as delivered, consistent with the chronic decision.
  Sessions undelivered at the order's expiry are forfeited; the order closes as partially fulfilled.
- LOOP CLOSURE: completion returns a report to the ordering doctor. For a REFERRAL this is MANDATORY —
  an open referral loop is the classic outpatient safety failure and the state machine already models
  closure. Surface open loops in the doctor's worklist.
- IDENTITY AT THE COUNTER: reuse the phase-26 Gate 6 card-number path — SECOND IDENTIFIER REQUIRED,
  minimum-necessary view, audited retrieval.
- Kong routes, compose entry, scope grants; route-coverage guard green.
ACCEPTANCE: provider A cannot see provider B's orders (test written first); sessions consume partially
and idempotently under parallel requests; a referral cannot close without a report; the payload carries
no diagnosis beyond the doctor's chosen context; identity verification needs two identifiers and audits.
```

## Gate 3 — OP Procedures tab in the encounter + History tab

```text
- DoctorEncounter.tsx: new tab beside Prescription / Labs / Radiology, using the shared order composer
  (extract it if labs/radiology currently duplicate it — do not add a third copy).
- History tab: new OP Procedures section alongside the existing ones, same projection and sensitivity
  rules as its siblings. No new access path.
- Bilingual AR/EN, RTL, axe clean against POPULATED fixtures.
ACCEPTANCE: procedures can be ordered from the encounter and appear in history with the same gating.
```

## Gate 4 — Per-line service history (one endpoint, one component)

```text
Read ../45 §4 and ../39 §1. Every service line — prescription, lab, radiology, OP procedure, and every
history tab — gets an icon opening a modal with that service's full history for this patient.

THIS IS AN AGGREGATION SURFACE. Built naively it becomes a side door around the ../37 §6 sensitivity
gate. Rules:
- ONE endpoint: GET /api/v1/patients/{beneficiaryId}/service-history?serviceType=&code=&page=
  Composed SERVER-SIDE under the CALLER'S token; the payload contains only what the caller may see —
  a withheld field is ABSENT from the JSON, never hidden in the client.
- THE SENSITIVITY GATE STILL BINDS: a restricted result renders existence-only (date, service, actor,
  branch, "restricted") with the request-access action. A history modal that reveals what the results
  inbox withholds defeats the whole gate. Add the test.
- Treating relationship, provider ownership and branch scope all still apply — this is an INTERSECTION
  of existing rules, never a union.
- Every open writes an audited PHI read naming the patient and the service code.
- THREE STATES, distinctly: has history · no previous occurrences · could not load. "Could not load"
  must never render as "none" — a clinician reading "no previous tests" when the service was simply
  unreachable will re-order unnecessarily or miss a trend.
- Where results are numeric AND the caller may see them, show the TREND (sparkline + table). That is
  the clinical value of the feature; the data table stays in the DOM alongside any chart (../12 §7).
- ONE shared React component consumed by all tabs. Not one per tab.
ACCEPTANCE: same modal from every tab; restricted results existence-only; unavailable ≠ empty; audited;
axe clean EN+AR.
TESTS: per-role projection over the SERIALIZED payload, sensitivity-gate test (registry-pinned),
three-state rendering, audit assertion.
```

## Gate 5 — Acute / chronic prescriptions and refill windows

```text
Read ../45 §5. Use superpowers brainstorming on the window model first, then TDD the allocation maths —
it is pure arithmetic with exact expected values.

MASTER DATA (supervisor-configurable, NOT an enum):
- refill_frequency(code, months int, name_en, name_ar, is_active, sort_order) seeded Monthly(1),
  Every2Months(2), Every3Months(3). The Approval Supervisor (phase 27 portal) administers it — adding
  "every 6 months" must be a data change, not a release.

PRESCRIPTION MODEL:
- prescription: kind CHECK IN ('Acute','Chronic') NOT NULL DEFAULT 'Acute',
  refill_frequency_code NULL, duration_days int NULL, valid_from date, valid_until date.
  CHECK: kind='Chronic' REQUIRES refill_frequency_code IS NOT NULL AND duration_days > 30.
  A 14-day course is not chronic — reject with a clear message, do not silently accept.
- prescription_dispense_window(window_id, prescription_id, window_no, scheduled_open_date,
  opens_at date (= scheduled - early tolerance), closes_at date, allocated_quantity numeric,
  dispensed_quantity numeric DEFAULT 0, status CHECK IN
  ('Pending','Open','Dispensed','PartiallyDispensed','Missed','Blocked'), blocked_reason NULL,
  + audit/history). Per LINE, not per prescription — lines can have different durations.
- Early tolerance configurable, default 5 days.

ALLOCATION (pure domain, TDD — libs/clinical-validation or a new libs/prescribing):
  1. total = dose x timesPerDay x durationDays, in PRESCRIBING UNITS (Gate 6)
  2. round the TOTAL to the dispensable unit for the form (splittable -> sub-unit; non-splittable ->
     round UP to whole items)
  3. windows = ceil(durationDays / (frequencyMonths * 30))
  4. allocate integers across windows by largest-remainder, HIGHEST FIRST
  THE ALLOCATION MUST SUM EXACTLY TO THE TOTAL. Round ONCE at the total, NEVER per window — rounding
  each window independently lets the sum drift above the prescribed amount, over-supplying the patient
  and over-consuming the benefit.
  Worked cases that must be tests: 90 days monthly 1x3/day -> 3 windows of 90; 100 units over 3 windows
  -> 34/33/33; a non-splittable inhaler over 3 windows -> whole canisters summing to the rounded total;
  90 days every-2-months -> 2 windows (60 days' worth, then 30).

DISPENSING BEHAVIOUR:
- ONE authorisation for the whole script (do NOT create one per window).
- ELIGIBILITY RE-VALIDATED AT EACH DISPENSE: a member whose policy lapsed in month 2 is BLOCKED at the
  pharmacy — status Blocked with a reason, the script is NOT cancelled, and it resumes if eligibility is
  restored. Blocked is distinct from Missed because it is not the patient's doing and the case team
  needs to see it.
- LIMITS CONSUMED PER DISPENSE, as collected — never the full 90 days upfront. The consume path must
  handle partial release against a single authorisation; reuse the existing atomic consume with the
  window as the idempotency subject.
- A window CANNOT be dispensed before opens_at: refuse with the open date named, not a generic error.
- A sweeper marks a window Missed when closes_at passes undispensed; the quantity is FORFEITED and
  cannot be claimed later. Mirror the ReportAccessExpirySweeper pattern.
UI: toggle Acute/Chronic; chronic reveals the frequency combobox; show the computed window schedule
with per-window quantities BEFORE submit, so the doctor sees 34/33/33 and can adjust.
ACCEPTANCE: every worked case above; allocation always sums to the total; early dispense refused;
lapsed member blocks without cancelling; a missed window cannot be recovered; limits move only on
collection.
```

## Gate 6 — Prescribing unit, pack size, splittability

```text
Read ../45 §6. INSPECT "Master Lists/egyptian-drug-list_5.xlsx" FIRST and report the column mapping
before writing the loader — as done for drug_indication. Do not assume column names.

- masterdata.drug gains: prescribing_unit varchar(16) CHECK IN ('Tablet','Capsule','ML','Puff','Spray',
  'IU','Drop','Sachet','Suppository','Vial','Ampoule','Patch','Gram'), pack_size numeric NULL,
  pack_unit varchar(16) NULL, is_pack_splittable bool NULL, unit_data_incomplete bool GENERATED or
  maintained by the loader.
- is_pack_splittable DEFAULTS FROM THE DOSAGE FORM but is OVERRIDABLE per product — the form is a good
  heuristic and a poor law. Tablets/capsules/sachets splittable; inhalers, pens, vials, ampoules,
  patches not.
- Loader maps the sheet's pack/unit columns; rows missing a required field set unit_data_incomplete and
  are LISTED in the load report — not silently defaulted.
- QUANTITY CALCULATION uses these: dose x timesPerDay x durationDays -> prescribing units -> packs where
  the pack cannot be split (ceil). Insulin in IU resolves to whole pens (a 3 mL 100 IU/mL pen = 300 IU);
  a spray in puffs resolves to whole canisters.
- MISSING UNIT DATA ⇒ the quantity check returns NotChecked NAMING THE MISSING FIELD, never a guessed
  quantity. A silently wrong quantity is a dispensing error, and this platform's rule is that absence of
  data is never a clean result.
ACCEPTANCE: sheet mapping documented; insulin/spray/tablet cases compute correctly; incomplete rows
report NotChecked naming the field; load report lists them.
```

## Gate 7 — Lowest-price label and availability

```text
Read ../45 §7 — the two corrections here matter more than the feature.

EQUIVALENCE GROUP: same ACTIVE INGREDIENT + SAME STRENGTH + SAME DOSAGE FORM/ROUTE. Ingredient alone is
not a valid group — a 500 mg tablet and a 250 mg/5 mL syrup share an ingredient and cannot be compared.
Use the Gate-3-of-phase-28 ingredient model if it has landed; otherwise scientific_name + strength + form.

COMPARE PRICE PER PRESCRIBING UNIT, NOT PACK PRICE: price_egp / pack_size. A 20-tab pack at 100 EGP is
MORE expensive per tablet than a 30-tab pack at 120 EGP; labelling by pack price would mislead a
prescriber trying to save a beneficiary money — the exact opposite of the feature's purpose.
- Ties: ALL members of the tie get the label.
- DERIVED, not authored: recompute whenever prices load; store with computed_at so a stale label is
  detectable. Index the grouping key.
- UI: a compact "Lowest price" chip in the drug combobox option row, beside the price already shown.

AVAILABILITY:
- availability varchar CHECK IN ('Available','Unavailable','Unknown') NOT NULL DEFAULT 'Unknown'.
  NOT a boolean. A boolean defaulting to false renders the entire catalogue as out of stock on day one,
  and prescribers would learn to ignore the indicator before it ever carried real data.
- UNKNOWN RENDERS NOTHING — no badge, no warning. Only a positive 'Unavailable' shows a badge, with the
  ATC-alternatives list offered alongside.
ACCEPTANCE: per-unit comparison proven with the 20-at-100 vs 30-at-120 case; ties all labelled;
Unknown renders nothing; Unavailable shows a badge with alternatives.
```

## Gate 8 — Docs, registry, ADR

```text
- ../22 gains order_type 'Procedure', refill_frequency, prescription.kind/refill_frequency_code/
  duration_days/valid_from/valid_until, prescription_dispense_window, drug.prescribing_unit/pack_size/
  pack_unit/is_pack_splittable/availability/lowest_price flags; ../23 gains the window state machine;
  ../14 gains the OP Procedures tabs and the service-history modal; 00-README-INDEX + README gain doc 45.
- BUILD-STATUS gains 29.1-29.8.
- docs/quality/invariant-registry.yaml: allocation-sums-to-total, missed-window-forfeited,
  limits-consumed-per-dispense, service-history-respects-sensitivity-gate, no-EM-code-orderable,
  missing-unit-data-is-NotChecked, availability-defaults-unknown.
- ADR-0029: display-only rename and why identifiers were left alone; Procedure as an order type rather
  than a new service; the four chronic decisions with their rationale; per-unit price comparison.
ACCEPTANCE: docs true; registry entries have named tests; ADR merged.
```

---

## Guardrails
- **Round once, at the total.** Never per window. The allocation must sum exactly to the prescribed total.
- **One authorisation, per-dispense eligibility, per-dispense limit consumption.** Do not create an authorisation per window.
- **Missed ≠ Blocked.** Forfeited by the patient vs stopped by the system — different statuses, different visibility.
- **The service-history modal weakens no gate.** Intersection of existing rules, never a union.
- **E/M creates a Referral, never a Procedure** — and a referral loop must close with a report back.
- **An external provider sees only its own rows.** Write the two-provider test before the queue endpoint.
- **Clinical context reaches an external provider only by the ordering doctor's explicit choice**, recorded and audited.
- **Absence of data is never a clean result** — missing unit data, unavailable service history and unknown availability each render as themselves, not as OK/none.
- **Never rewrite the audit chain.** Historical rows keep their old identifiers; readers use a display alias.
- Full suite green after each gate (`./dotnet.sh test HbmpPlatform.sln -c Release --with-db` + `pnpm -r test`), including the untouched min-necessary, RLS, sensitivity and consume-concurrency suites.

## Done when
- [ ] Radiology rename complete across role, scopes, enums, events, routes and strings, via expand → backfill → switch → contract; a pre-switch token still authorises and a pre-switch outbox event is still consumed during the window; the audit chain verifies unbroken with old rows behind a display alias; the token-contract test is green against both fixtures.
- [ ] Surgery/Medicine create `Procedure` orders through the existing consume/authorise/claim path; **E/M creates a Referral** with mandatory loop closure; the CPT routing map is reconciled against the published ranges with a report.
- [ ] Procedure **type** is master data with `is_session_based`; the sessions field follows the flag, not the name; type/code mismatch is refused; a 10→6 partial approval yields **6** deliverable sessions.
- [ ] External Provider Portal live: provider A cannot see provider B's rows (test written first); sessions consume **one at a time**, atomic and idempotent with a per-session key (a replayed session does not burn two); progress shown in both views; unused sessions forfeited at expiry; referral loops close with a report; the payload carries no diagnosis beyond the doctor's chosen context; identity needs a second identifier and is audited.
- [ ] Encounter and History both carry OP Procedures with sibling gating.
- [ ] One service-history endpoint and one component serve every tab; restricted results are existence-only with request-access; three states distinct; every open audited; trends shown where permitted.
- [ ] Acute/Chronic toggle; chronic requires > 1 month and a frequency; frequency is supervisor-configurable master data.
- [ ] Allocation: 90/monthly → 3×90; 100/3 → 34/33/33; non-splittable → whole units; **sum always equals the total**; schedule shown before submit.
- [ ] One authorisation; eligibility re-validated per dispense; lapsed member → Blocked, script intact; limits consumed only on collection; early dispense refused; missed window forfeited by sweeper.
- [ ] `prescribing_unit`, `pack_size`, `is_pack_splittable` loaded with a mapping report; insulin/spray/tablet quantities correct; incomplete data → NotChecked naming the field.
- [ ] Lowest-price computed **per prescribing unit** within ingredient + strength + form, ties included, `computed_at` stored.
- [ ] `availability` defaults to Unknown and renders nothing; Unavailable shows a badge with alternatives.
- [ ] ADR-0029 merged; registry entries named; docs updated.
