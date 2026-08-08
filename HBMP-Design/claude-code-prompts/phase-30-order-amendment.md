# Phase 30 — Amend & cancel signed orders and prescriptions

**Goal:** Let a prescriber cancel or amend a **signed** prescription, lab order, radiology order or OP procedure for as long as it has not been consumed — propagating the change to everyone holding it. Chronic prescriptions additionally allow **duration and frequency** edits. Plus two new session-based procedure types.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Design: [`../46-order-amendment-and-cancellation.md`](../46-order-amendment-and-cancellation.md)

> **Independent phase.** It assumes the earlier phases have landed but adds nothing to them; run it on its own.
>
> ⚠️ **Two things make this harder than it looks.**
> **(1) The check and the write are a race.** "Not yet dispensed" is not a state you can read and then act on — a pharmacist can start dispensing between the doctor's click and the server's write. Read-then-write here is the lost-update this platform already defends against on the consume path.
> **(2) Signed clinical records are not editable.** Amend means **supersede**; the original is never mutated. Anything else destroys the answer to "what was actually prescribed?", which is the question asked when something goes wrong.
>
> **VERIFY BEFORE BUILDING.** `prescription.status` already includes `Cancelled` in its CHECK constraint, and appointments already have a cancel path. Audit what exists for each of the four order kinds first and report it — extend what is there rather than adding a parallel mechanism.

## Skills to activate
> **Superpowers:** **brainstorming** before Gate 3 (the chronic re-allocation on a partially-dispensed script has real alternatives and is hard to change later); **test-driven-development** throughout — the concurrency and re-allocation logic are pure and belong in Domain, written test-first; **writing-plans** for the migration order across pharmacy/orders/approvals.
> **Project skills:** `mersal-platform-architect`, `refugee-healthcare-management` (always-on), `clinical-workflow-designer`, `pbm-adjudication-engine`, `healthcare-business-rules-engine`, `healthcare-database-architect`, `healthcare-uiux-designer`.

## Context — read first
- [`../46-order-amendment-and-cancellation.md`](../46-order-amendment-and-cancellation.md) — **AUTHORITATIVE**: §2 (the race), §3 (partial consumption), §4 (chronic), §5 (authorisation scope), §6 (propagation), §9 (invariants).
- [`../23-state-machines.md`](../23-state-machines.md) · [`../45-encounter-and-prescription-adjustments.md`](../45-encounter-and-prescription-adjustments.md) §5 (window allocation) · [`../19-audit-strategy.md`](../19-audit-strategy.md).
- **Existing code:** `services/pharmacy/{Api/Prescriptions.cs,Api/Dispensing.cs,Domain/DispenseExecutor.cs,Infrastructure/Migrations/*}`, `services/orders/{Api/Consume.cs,Domain/OrderConsume.cs}` (the guarded-transition pattern to copy), `services/approvals/Api/Decisions.cs`, `libs/events/**`, `apps/web/src/screens/DoctorEncounter.tsx` + `screens/prescribing/**`.
- **Run DB-gated tests with `./dotnet.sh test --with-db`.**

## INVARIANTS (../46 §9)
1. **Nothing signed is mutated.** Amend supersedes; cancel transitions; nothing is deleted.
2. **The consumed portion is immutable** — amendment applies only to the unconsumed remainder.
3. **State check and write are one atomic guarded transition**; a conflict reports exactly what happened.
4. **A chronic re-allocation still sums exactly** to the new total, which is never below the dispensed amount.
5. Amendments **beyond the approved scope** return to pending authorisation; within it, they do not.
6. **Propagation updates the fulfilling party's queue** — a notification alone is not propagation.
7. Every amendment/cancellation carries a **coded reason** and an actor, and is audited.
8. Cancelled and superseded records **stay visible** in history.

---

## Gate 0 — Audit what already exists (small commit, report first)

```text
Before writing anything, report per order kind (prescription, lab order, radiology order, OP procedure):
- Which statuses exist in the CHECK constraint, and which are terminal.
- Whether any cancel/void/amend endpoint exists today, and what it does.
- Where the consume/dispense guarded transition lives for that kind, and what it guards on
  (xmin? row_version? unique idempotency key?).
- Which events are published on consume, and WHO CONSUMES THEM (there are ~40 published event types
  with no subscriber — do not add to that pile).
- What the fulfilment-side queue query filters on, since that is what must change on cancellation.
Output a short table. Extend what exists; do not build a parallel mechanism.
```

## Gate 1 — Supersede-and-cancel model (append-only, line level)

```text
Read ../46 §1 and §3. AMENDMENT IS AT LINE LEVEL, not order level — the amendable scope is whatever has
not been consumed.

MIGRATION (per owning service):
- Add to the line/order tables: version_no int NOT NULL DEFAULT 1, supersedes_id uuid NULL,
  superseded_by_id uuid NULL, amendment_reason_code varchar(32) NULL, amendment_reason_text varchar(300)
  NULL, amended_by uuid NULL, amended_at timestamptz NULL. Plus 'Superseded' in the status CHECK.
- amendment_reason(code, name_en, name_ar, applies_to, is_active) seeded: PrescribingError,
  DoseCorrection, PatientDeclined, ClinicalChange, Duplicate, DrugUnavailable, NotEligible, Other.
  CODED, not free text alone — the codes are what make "how often do we cancel and why" answerable, and
  they feed the medical director's quality reporting. Free text is additional, not instead.
- NEVER UPDATE A SIGNED ROW'S CLINICAL CONTENT. Amend = insert the new version + mark the original
  Superseded. The *_history twins and the audit chain are untouched by this; do not rewrite either.

RULES:
- A whole-order cancel is "cancel every still-cancellable line". If some lines are already consumed,
  report PARTIAL SUCCESS plainly — which lines were cancelled, which could not be and why. Do not fail
  the whole request, and do not silently do half.
- Cancelled and Superseded rows REMAIN VISIBLE in history and in the service-history modal with their
  status and reason. A cancelled antibiotic is clinically meaningful information.
ACCEPTANCE: original never mutated; new version links via supersedes_id; whole-order cancel with one
dispensed line reports partial success; cancelled rows still visible in history.
```

## Gate 2 — The guarded transition (this is the correctness core)

```text
Read ../46 §2. Copy the pattern from services/orders/Domain/OrderConsume.cs — do NOT invent a second one.

- ONE atomic statement: UPDATE ... SET status = 'Cancelled'/'Superseded' WHERE line_id = @id
  AND status IN (<amendable states>) AND row_version = @expected. Zero rows affected = someone got there
  first. Never read-then-write.
- THE CONFLICT RESPONSE MUST BE SPECIFIC: 409 problem+json naming what happened — "line 2 was dispensed
  at 14:32 by Maadi Pharmacy". A doctor told only "someone else changed this" will simply retry, which
  is how a cancelled-then-dispensed drug happens.
- THE MIRROR: a dispense/consume attempt against a Cancelled or Superseded line fails with the
  cancellation reason and actor, not a generic error. Add it to the dispense path.
- Idempotency-Key required on cancel and amend, stable per intent (not per attempt) — a double-tapped
  cancel must not create two amendment records.
- CONCURRENCY TEST, written first: N parallel cancels + dispenses of the same line produce exactly one
  winner; the state is consistent; the loser's message names the winner. Reuse the existing
  DispenseConcurrencyTests / consume harness — do not write a new one. Registry-pin it.
ACCEPTANCE: exactly one winner under parallel cancel/dispense; conflict message is specific; dispensing
a cancelled line fails with the reason; replayed cancel applies once.
```

## Gate 3 — Chronic: edit duration and frequency

```text
Read ../46 §4 and ../45 §5. Brainstorm first, then TDD the re-allocation — it is pure arithmetic with
exact expected values.

THE PRINCIPLE: WHAT WAS DISPENSED IS A FACT AND IS NEVER RECALCULATED.
1. Dispensed windows keep their quantities EXACTLY as dispensed.
2. Remaining duration is recomputed from the new end date.
3. Remaining quantity is re-allocated across the new remaining windows by the same largest-remainder,
   highest-first method — and MUST STILL SUM EXACTLY to the new total (round once at the total, never
   per window; that rule does not relax on amendment).
4. New total BELOW the already-dispensed amount is REFUSED with a clear message — it implies
   un-dispensing.
5. Frequency changes reschedule only FUTURE windows; a collected window's dates are history.

THE EDGE CASE THAT MUST NOT BE SILENT: reducing duration to <= 1 month means the script no longer meets
the chronic definition. Either refuse, or convert to Acute WITH AN EXPLICIT PRESCRIBER CONFIRMATION —
and record the conversion, because it changes the dispensing pattern the patient was told to expect.
Do not silently keep a "chronic" script that is not chronic.

WORKED CASES AS TESTS: 90 days monthly, window 1 (90 units) dispensed, amended to 60 days -> window 1
untouched, one remaining window of 90, total 180; same script amended to 120 days monthly -> windows 2-4
re-allocated summing to the new remainder; amend to a total below 90 -> refused; amend 90 days to 25
days -> chronic-definition prompt.
ACCEPTANCE: every worked case; allocation always sums; dispensed windows byte-identical after amendment.
```

## Gate 4 — Authorisation scope

```text
Read ../46 §5. Whether an amendment needs re-approval depends on ONE question: does it stay inside what
was approved?

- WITHIN the approved scope (reduce quantity, shorten duration, cancel a line) -> the authorisation
  REMAINS VALID; do not trouble the approval team.
- BEYOND it (increase quantity, change the drug or service code, extend duration) -> the authorisation's
  basis no longer holds. The order returns to PENDING AUTHORISATION and approvals is notified that a
  previously approved item changed, with a before/after.
- Implement as a pure comparison of the amended scope against authorization.requested_scope /
  the approved scope, reusing the existing partial-scope subset logic in
  approvals/Domain/DecisionRules.cs ValidatePartialScope rather than writing a second comparator.
- Getting this backwards is costly BOTH ways: treat everything as re-approvable and you flood the
  queue; treat nothing as re-approvable and you have built a way to get approval for one thing and
  dispense another. Test both directions.
ACCEPTANCE: in-scope reduction keeps the authorisation and notifies nobody; out-of-scope change returns
to pending authorisation with a before/after visible to the approver.
```

## Gate 5 — Propagation (the queue must change, not just a notification)

```text
Read ../46 §6. THE FAILURE MODE IS A CANCELLED ORDER STILL SITTING IN THE LAB'S QUEUE BECAUSE ONLY AN
EMAIL WAS SENT.

- Publish domain events: OrderLineCancelled / OrderLineAmended / PrescriptionLineCancelled /
  PrescriptionLineAmended (name them consistently with the existing catalogue — check Gate 0's findings).
- EVERY EVENT PUBLISHED HERE MUST HAVE A REAL SUBSCRIBER that updates the fulfilment-side read model, so
  the item LEAVES OR CHANGES IN THE PROVIDER'S QUEUE. Assert propagation against the QUEUE ENDPOINT,
  not against the notification. Run the phase-24 event-symmetry gate before and after; do not add to the
  ~40 orphaned event types.
- Notify, in addition to (not instead of) the queue update:
  * fulfilling provider (pharmacy / lab / radiology / procedure centre) — most urgent, they may be
    preparing it now;
  * the beneficiary — especially chronic, where they may be travelling to collect;
  * the ordering doctor — confirmation, and notification if SOMEONE ELSE amended their order;
  * the case manager where assigned;
  * approvals only when out of scope (Gate 4).
- CLAIMS: if anything was already claimed, the amendment is a RECONCILIATION EVENT, not a silent edit.
  Emit it into the claims reconciliation path (../36) rather than adjusting a claim in place.
ACCEPTANCE: the cancelled line is gone from the provider queue endpoint within the consumer's SLA;
notifications delivered per the table; symmetry gate green; a claimed item produces a reconciliation
entry.
```

## Gate 5b — Notes on prescriptions, labs, radiology and procedures

```text
Read ../46 §7b. Every order line gains notes — the instruction that travels with an order ("fasting
sample", "left knee, post-op review") and the answer that comes back ("sample haemolysed, please
repeat", "patient did not attend").

REUSE THE EXISTING NOTES MODEL. ../38 §5 already defines one for policies and members — append-only,
signed with the author's name, timestamped, CANCELLABLE BUT NEVER DELETABLE, class-projected — with a
shared Notes Panel component (apps/web PolicyPanels). Order notes are the SAME model on a different
subject. Do NOT write a fourth notes implementation: two mechanisms means two behaviours for "cancel a
note" and two answers to "who can read this".

MIGRATION (per owning service, or one shared table keyed by (subject_type, subject_id) — decide and
record it in the ADR):
- order_note: note_id, subject_type CHECK IN ('PrescriptionLine','OrderLine'), subject_id,
  visibility CHECK IN ('ToFulfiller','Internal','FromFulfiller') NOT NULL DEFAULT 'ToFulfiller',
  body varchar(500) NOT NULL, author_user_id, author_display_name, created_at,
  status CHECK IN ('Active','Cancelled'), cancelled_by, cancelled_at, cancel_reason,
  + tenant RLS + *_history twin. APPEND-ONLY: no UPDATE of body, ever.

THREE VISIBILITY CLASSES, because the reader differs:
  ToFulfiller   — clinician -> the pharmacy/lab/radiology/centre holding the order, + internal clinical roles
  Internal      — clinician -> internal clinical roles ONLY. THE EXTERNAL PROVIDER NEVER SEES THIS.
  FromFulfiller — provider -> the ordering clinician + internal clinical roles
Default is ToFulfiller (the common case is an instruction meant to be read). An external centre seeing a
clinician's internal reasoning would widen the deliberately narrow projection built in ../45 §2b — add
the projection test over the SERIALIZED payload proving an external provider principal receives no
Internal note.

SENSITIVITY IS INHERITED, NOT RE-DECIDED: a note on a sensitive examination (../37 §6) inherits that
order's sensitivity. A note on a mental-health investigation must not be readable by someone who cannot
read the result — otherwise the note is the gap in the gate. Add the test.

A NOTE IS NOT AN AMENDMENT: adding one does NOT supersede the order, does NOT create a version, and does
NOT invalidate an authorisation. Only clinical content (drug, dose, quantity, duration, service code,
sessions) triggers the Gate 1 supersede path. Conflating them would send every "fasting sample" note
back to the approval queue. Add the test asserting a note leaves version_no and authorisation status
untouched.

A NOTE IS NOT A CLINICAL RECORD: cap the length, and put helper text at the point of writing saying so.
A free-text box on an order attracts clinical findings, and anything written there sits OUTSIDE the EMR,
outside the sensitivity classification, and outside the record the next clinician reads — they open the
encounter and never see it. Route clinical findings to the encounter note.

UI: notes on the line in the doctor's view; PROMINENT in the fulfiller's queue detail (an instruction
nobody reads is worthless — not behind a collapsed panel); present in the service-history modal.
Reuse the shared Notes Panel; bilingual AR/EN, RTL, axe clean on populated fixtures.
ACCEPTANCE: notes append-only and signed; cancel marks, never deletes; an external provider cannot read
an Internal note; a note on a sensitive order inherits its gate; adding a note neither supersedes nor
re-triggers authorisation; notes visible to the fulfiller without hunting.
```

## Gate 5c — Encounter timeline starts at check-in

```text
Read ../46 §7c. The encounter timeline opens at "Visit started". It must open at "Checked in", then
"Visit started", then the rest.

- Check-in lives on emr.appointment (recorded by reception); the encounter begins later. The timeline is
  a COMPOSED VIEW over both aggregates — do NOT copy check-in data onto the encounter.
- Each entry carries actor + timestamp + branch, like every other timeline in the platform.
- DERIVE WAITING TIME (visit started - checked in) and surface it: on the timeline, and on the branch
  dashboard beside the checked-in and no-show counts. It is the number a clinic manager actually wants
  and it now costs nothing.

THREE CASES, KEPT DISTINCT:
 1. Checked in then seen -> both entries, waiting time shown.
 2. NO CHECK-IN RECORDED (walk-in taken straight in, or a missed step) -> the timeline says "no check-in
    recorded". It does NOT silently begin at Visit started as though the two were the same moment.
    Absence of a record is not evidence the step happened — the platform's standing rule.
 3. CHECK-IN TIMESTAMPED AFTER VISIT START (retroactive entry) -> show both AS RECORDED and FLAG the
    inconsistency. Do NOT reorder them into a plausible sequence. Silently sorting bad timestamps into a
    tidy story is how you lose the ability to notice the process is broken.
ACCEPTANCE: normal path shows both entries with waiting time; a walk-in with no check-in says so
explicitly; an out-of-order pair is shown as recorded and flagged, not reordered.
TESTS: the three cases; waiting-time arithmetic across midnight and across a branch timezone.
```

## Gate 6 — Authority, UI, and the two new procedure types

```text
AUTHORITY (../46 §7):
- The AUTHORING PRESCRIBER by default; ANOTHER TREATING CLINICIAN with a reason (cover happens, and a
  doctor who has gone home must not block a correction); NEVER reception, call centre, or the fulfilling
  provider. A pharmacy that disagrees raises a clarification — it does not edit a prescription.
- Bounded by the order's own validity: an expired order is not amendable, it is expired.
- Enforce via the existing ABAC treating-relationship condition; add the denial tests.

UI:
- Cancel / Amend actions on each line in the encounter and in the doctor's order & prescription
  worklists, with the reason picker (coded + free text) and a confirmation naming exactly what will
  change.
- Consumed lines show the action DISABLED with the reason visible ("dispensed 14:32, Maadi Pharmacy") —
  not hidden. A hidden control makes the doctor think the feature is missing.
- Chronic amendment shows the recomputed window schedule BEFORE confirming, with dispensed windows
  clearly marked immutable.
- Superseded/cancelled rows remain in history with a four-cue status chip and their reason.
- Bilingual AR/EN, RTL, axe clean against POPULATED fixtures.

PROCEDURE TYPES: seed `OccupationalTherapy` and `SpeechTherapy` in masterdata.procedure_type, both
is_session_based = true — the same shape as Physiotherapy. This is a DATA change only; if it needs a
code change, the ../45 §2 flag was not implemented as designed and that is the bug to fix.
ACCEPTANCE: non-treating clinician refused; pharmacy refused; consumed lines show a disabled action with
the reason; chronic preview shown before confirming; both new types appear with the sessions field and
no code change.
```

## Gate 7 — Docs, registry, ADR

```text
- ../23 gains Superseded + the amendment transitions on all four order lifecycles; ../22 gains the
  version/supersedes columns and amendment_reason; ../17 gains the endpoints; ../11 confirms no new
  scope is needed (reuse rx:write / orders:write) — state it explicitly either way;
  00-README-INDEX + README gain doc 46; BUILD-STATUS gains 30.0-30.7.
- docs/quality/invariant-registry.yaml: signed-records-are-superseded-not-mutated,
  consumed-portion-immutable, cancel-dispense-single-winner, chronic-reallocation-sums,
  out-of-scope-amendment-returns-to-authorisation, cancellation-updates-provider-queue.
- ADR-0030: supersede-not-edit; line-level scope; the guarded-transition choice; the in-scope /
  out-of-scope authorisation rule; why propagation is a consumed event rather than a notification.
ACCEPTANCE: docs true; registry entries have named tests; ADR merged.
```

---

## Guardrails
- **Never mutate a signed clinical record.** Supersede, cancel, append — never UPDATE clinical content in place, and never rewrite history or the audit chain.
- **Never read-then-write** the consumed check. One guarded statement, and a specific conflict message.
- **The consumed portion is immutable** in every scenario — dispensed lines, collected windows, delivered sessions.
- **Round once at the total** on chronic re-allocation; the sum rule does not relax because it is an amendment.
- **A notification is not propagation.** Assert against the queue endpoint.
- **No new orphaned events** — every event published here has a subscriber; run the symmetry gate.
- **Coded reason, mandatory, on every amendment and cancellation.**
- **Notes reuse the doc-38 model** — no fourth notes implementation. A note is an annotation, never an amendment, and never a clinical record.
- Full suite green after each gate (`./dotnet.sh test HbmpPlatform.sln -c Release --with-db` + `pnpm -r test`), including consume-concurrency, min-necessary, RLS, sensitivity and chronic-allocation suites.

## Done when
- [ ] Gate 0 audit reported; existing mechanisms extended, none duplicated.
- [ ] Signed orders and prescriptions can be cancelled or amended while unconsumed; originals are Superseded/Cancelled, never mutated; versions link; cancelled rows stay visible in history.
- [ ] Line-level scope: partial cancel of a partly-dispensed order reports partial success plainly.
- [ ] Parallel cancel vs dispense yields exactly one winner; the loser's message names what happened; dispensing a cancelled line fails with the reason; replayed cancel applies once.
- [ ] Chronic duration/frequency editable; dispensed windows byte-identical; remainder re-allocated and summing exactly; below-dispensed total refused; ≤1-month reduction refuses or converts to acute with confirmation, recorded.
- [ ] In-scope amendments keep the authorisation; out-of-scope ones return to pending authorisation with a before/after.
- [ ] The cancelled item **leaves the provider's queue** (asserted on the queue endpoint); beneficiary, doctor and case manager notified; claimed items raise a reconciliation entry; symmetry gate green.
- [ ] Authoring prescriber and treating clinicians may amend; reception, call centre and providers refused; consumed lines show a disabled action with a visible reason.
- [ ] Notes on every order kind reusing the doc-38 model (append-only, signed, cancellable, never deleted); an external provider cannot read an `Internal` note (projection test on the serialized payload); a note on a sensitive order inherits its gate; **adding a note neither supersedes the order nor re-triggers authorisation**; notes are prominent in the fulfiller's queue detail and present in the service-history modal.
- [ ] Encounter timeline opens at **Checked in → Visit started**, composed across appointment and encounter; waiting time derived and surfaced; "no check-in recorded" stated explicitly rather than assumed; an out-of-order pair is flagged, not silently reordered.
- [ ] `OccupationalTherapy` and `SpeechTherapy` seeded as session-based types **with no code change**.
- [ ] ADR-0030 merged; registry entries named; docs updated.
