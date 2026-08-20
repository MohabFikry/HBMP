# The prescriber's portal — audit and design

**Date:** 2026-08-20
**Branch:** `feat/prescriber-portal-audit` (off `master`, which now carries passes 1–4)
**Scope:** the `doctor` portal (`/clinician`) — the encounter workspace, orders, prescriptions, the results
inbox and result-access requests — plus the `emr`, `orders` and `pharmacy` surfaces behind them.

---

## 1. What this is

The fifth client-vs-service pass. One to four covered clinic management, the medical director, approvals and
claims, and finance with the pharmacy counter. This one covers the surface where clinical decisions are
made, which changes what the findings mean: the previous four passes found money that could not be
reconciled and screens that could not load. This one finds a check that reports a clean result it did not
perform, and a signed clinical note that cannot be corrected.

**Method.** For every endpoint the three services serve, ask which layer calls it: the SPA, another service,
or nothing. 34 endpoints came back apparently unreached; **five of those were verified as correct** and are
recorded in §2b so the next pass does not re-raise them.

---

## 2. The findings

Every claim below was verified against the code at the cited line, and two were narrowed after verification
rather than reported as first read.

### F1 — the interaction check never sees what the patient is already taking, and reports `Ok`

`ValidationRequest` carries `ActiveMedicationDrugIds`, documented as *"the beneficiary's current
medications, so interactions are checked against what they are already taking and not only across the lines
being written now"* (`ValidationInputs.cs:36-43`). `PrescriptionValidator.cs:234-245` iterates it, pairing
every written line against every active drug.

**Both production constructions pass `[]`** — `PrescriptionValidationService.cs:68` and `HttpClients.cs:118`.
There are no others outside tests.

The loop therefore runs zero times, and then every unflagged line is reported
**`ClinicalState.Ok` — "No interaction found (checked against Mersal's interaction list: N ingredient
pairs)"** (`PrescriptionValidator.cs:248-260`). A prescriber reading that sentence has no way to know the
cross-medication half of the check did not happen.

**Where this is NOT the phase-28 defect, and where it is.** Phase 28's "allergy check that always passes"
was silent — nobody knew. This is not silent at the call site: both constructions carry a comment saying
active medications are not sourced yet, and `HttpClients.cs:117` states the intent plainly — *"It produces
NotChecked findings, which are reported rather than assumed away."*

**That sentence is wrong, and it is the finding.** With an empty list nothing is flagged, so the line falls
through to the `Ok` branch. `NotChecked` is never produced. The gap is documented in the code and
**mis-stated in the product**: the honest engineering note and the sentence the prescriber reads say
different things, and only one of them is on screen in a consulting room.

The clinical case is the one the validator's own remarks describe: paracetamol written today, a
cold-and-flu compound already active from last month, a combined daily total over the hepatotoxic ceiling.
Same script, it is caught. Different scripts, it is not — and the screen says no interaction was found.

### F2 — a signed clinical note cannot be corrected

`POST /encounters/{id}/notes/{noteId}/addendum` is served (`ClinicalRecords.cs:291`) and is described in the
domain model as *"the ONLY way to correct after signing"* (`ClinicalRecords.cs:17-19`). The encounter
workspace tells the doctor so twice — *"This note is signed and can no longer be edited. Record a correction
as an addendum."* (`DoctorEncounter.tsx:198`) and again in the signing confirmation
(`DoctorEncounter.tsx:211`).

`HttpApiClient` has no method for it. `apps/web/src/screens/` has no control for it. The 409 the server
returns on an edit attempt — *"A signed note is immutable — record a correction as an addendum"* — names a
path the only client of this platform cannot take.

### F3 — "Ask for more" is a one-way door

`ReportAccessInbox.tsx:120` offers **Ask for more**, which posts `decision: "requestinfo"` and moves the
request to `InfoRequested`. `HttpApiClient.ts:684` maps that state to a chip and renders it.

`POST /report-access-requests/{id}/supply-info` — the only transition out — is unreached. Its own header
records why it was built (18.A4): *"A request that entered InfoRequested had NO path back, so the requester
could never answer the question and the release was permanently stuck."*

The server closed that. The client never got the verb, so from the product's point of view the defect is
still open — and the button that walks a user into it is one the product itself offers.

### F4 — `UnderReview` is unreachable from the product, and the SLA timer never starts

`POST /report-access-requests/{id}/review` (`ReportAccess.cs:168`) is unreached. Its header: *"route/pick-up:
Requested → UnderReview. Without this the state was unreachable and the decider's identity was never
recorded before the decision itself (23 §11)."*

**Narrowed after checking the state machine, rather than reported as first read.** Only one of those two
consequences is live. `ReportAccessWorkflow.IsDecidable` includes `Requested`
(`services/orders/Domain/ReportAccess.cs:81`) precisely *"so a decider who acts before the routing step is
not blocked — the service records the implicit pick-up as UnderReview first"*, and the decision handler sets
`DecidedBy` and `DecidedByRole` on that path (`ReportAccess.cs:116-117`). So the decider **is** recorded.

What is actually lost is the interval. `review` sets `DecidedBy` *when the timer starts*, which is what makes
"this request has been sitting with a named person for six hours" answerable. Without it every request
reads as unattended until the moment it is decided, and the SPA renders an "Under review" chip for a state
nothing can enter. That is a queue-management defect, not an attribution one — and the difference is worth
stating, because the endpoint's own header invites the stronger reading.

### F5 — order notes reach nothing, and prescriptions never got notes at all

Two defects in one feature, at different layers.

**F5a, client.** `orders/Api/Notes.cs` serves read, write and cancel on an investigation-order line
(`:46,:97,:163`) and `ProcedureProvider.cs:222` serves the fulfiller's read. The implementation is complete
and careful — class projection before serialization, sensitivity inherited from the line, a 500-character
cap with helper text explaining that clinical findings belong in the encounter note. **`HttpApiClient` has
no note method of any kind.** Design 46 §7b's three visibility classes, the append-only signed model and the
inheritance rule are all built and invisible.

**F5b, server.** Doc 46 §7b is titled *"Notes on prescriptions, labs, radiology and procedures"* and opens
*"Every order line gains notes"*. **`services/pharmacy/Api` has no note endpoints.** Prescriptions never got
them. This is a divergence from the design doc, not a wiring gap, and it is the one finding in this pass
that needs new server work rather than connection.

### F6 — chronic schedule amendment and prescription line-cancellation have no UI

`POST /{rxId}/lines/{lineId}/amend-schedule` (`Amendment.cs:166`) implements design 46 §4: duration and
frequency edits where dispensed windows keep their quantities exactly, the remainder re-allocates by
largest-remainder and must still sum exactly, a new total below what was already dispensed is refused, and
shortening below the chronic definition requires the prescriber's explicit `ConvertToAcute`.

`POST /{rxId}/cancel-lines` (`Amendment.cs:290`) is the prescription twin of orders' `withdrawOrder`, which
**is** wired (`HttpApiClient.ts:977`).

Neither is reached. `ChronicAmendExecutor` was debugged and improved in 31.5 — the allocation was dividing
by `pack_size` instead of `pack_content` and would have shown a ninety-day syrup course as 1,800 packs — for
a code path no user can reach.

### F7 — `medication_history` has no writer, and it is F1's missing source

`POST /beneficiaries/{id}/medication-history` has no caller: not the SPA, not any service. The table feeds
`/clinical`'s medication list and the FHIR `MedicationStatement` projection
(`FhirProjection.cs:69`, `ClinicalRecords.cs:755`), so both report "no medications" as a fact about every
patient on the platform.

`MedicationSource { Prescribed, SelfReported, External }` is why this matters beyond tidiness: the enum
exists to record medicines **Mersal did not prescribe**, which is exactly the input no query over Mersal's
own prescriptions can ever supply.

### F8 — `GET /prescriptions/{id}/dispensing` is unreached

The counter reads `/prescriptions/queue` and `/prescriptions/search` instead. Low severity, and the honest
answer may be deletion rather than connection — see §3.6.

### 2b. Verified as correct — do not re-raise these

Five endpoints look unreached from the SPA and are supposed to be:

| Endpoint | Called by | Why it must not be a SPA call |
|---|---|---|
| `GET /encounters/{id}/validation-context` | pharmacy (`HttpClinicalValidationPorts.cs:187,237`) | 28.2's whole point: the diagnosis list must come from the encounter, never from a request body |
| `GET /beneficiaries/{id}/clinical-context` | approvals (`HttpClinicalContextClient.cs:22`) | field-scoped review DTO, PHI-audited under purpose |
| `.../for-beneficiary/{id}` (orders, pharmacy, referrals) | profile (`ClinicalProviders.cs:146,182,240`) | the 360 is composed server-side under the caller's token |
| `GET /prescriptions/history/{id}` | orders (`PrescriptionHistoryClient.cs:78`) | service-to-service |
| `POST .../extend-validity` (orders + pharmacy) | approvals (`ValidityExtensionApplier.cs:56-57`) | only someone who may decide an authorization may move an expiry |

### 2c. Out of scope, flagged rather than absorbed

**The reception walk-in queue.** `emr/Api/Queue.cs` serves `/queues`, `call-next`, `requeue`, `remove` and
`complete` (phase 3.3). **Nothing calls any of them** — no service, no screen. The doctor's day list reads
appointments (`DoctorVisits.tsx:77`), not the queue. This is reception's portal, so it belongs to pass 6;
recorded here because it was found here.

---

## 3. Decisions

### 3.1 F1 — source the active-medication list, and never say `Ok` about a check that did not run

Two halves, and the second is the one that must not be skipped.

**The source is a union, not a table.** `ActiveMedicationDrugIds` is populated from **active Mersal
prescriptions ∪ recorded `medication_history` rows with `Status = Active`**. Neither alone is the answer:
prescriptions cannot know what a patient bought elsewhere, and history cannot stay current by itself.

**The coverage is stated, or the state is not `Ok`.** This follows the rule doc 44 §8 already established
for the interaction table itself — "coverage stated, not implied". Concretely:

- source available → the `Ok` sentence names it: *"No interaction found (checked against Mersal's
  interaction list: N ingredient pairs, and M current medications)."*
- source unavailable → `ClinicalState.Unavailable` for that half, exactly as the composition fetch already
  does. **Never `Ok`.** This is the phase-26 rule that deleted three silent catches, applied to the input
  rather than the fetch.
- no current medications recorded → say *that*, and distinguish it from having checked against a
  populated list. "Nothing recorded" and "nothing found" are different claims.

**Both call sites, not one.** `HttpClients.cs`'s legacy drug-set path gets the same treatment; its comment
claiming `NotChecked` findings is deleted rather than left to describe behaviour that does not exist.

### 3.2 F2 — an addendum control on every signed note

The workspace already renders the "signed, cannot be edited" state. It gains an **Add addendum** action
there, opening the same S/O/A/P composer the original note used, posting to the addendum endpoint. The
addendum renders beneath its original, indented and labelled with its author and time — never merged into
it, because the record's value is that the original text is still visible.

`HttpApiClient.ts:1184` already filters `addendumOfNoteId` out of the note list to pick the primary note;
that filter becomes the grouping key instead of a discard.

### 3.3 F3 + F4 — both transitions, and pick-up is explicit

**Supply info.** A requester whose request is `InfoRequested` sees the reviewer's question and a
**Respond** control that posts the supplement. The server appends and never overwrites, so the screen says
so: the original justification stays visible above the supplement.

**Pick-up is a button, not a side effect of opening the screen.** `review` sets `DecidedBy` and starts the
SLA timer. Firing it on render would attribute the review to whoever scrolled past, which is the opposite of
what 18.A4 added it for. **Take under review** is an explicit action, and the decision controls stay
available without it — the server does not require pick-up before a decision and neither will the screen.

### 3.4 F5 — one notes model, extended to order and prescription lines

**Client (F5a).** `NotesPanel` (`PolicyPanels.tsx:213`, doc 38 §5's model) is extended to a new scope rather
than reimplemented — doc 46 §7b requires exactly this and says why: *"A second notes mechanism means two
behaviours for 'cancel a note' and two answers to 'who can read this'."* It gains a visibility selector
defaulting to `ToFulfiller`, and the 500-character cap with the server's own helper text.

**Server (F5b).** Prescription-line notes are added to `pharmacy` as a port of `orders/Api/Notes.cs` — same
entity shape, same class projection, same inheritance rule, `SubjectType = "PrescriptionLine"`. A migration
adds `pharmacy.prescription_note`.

**Where notes appear, including outside this pass's portal.** Doc 46: *"Notes appear on the line in the
doctor's view, prominently in the fulfiller's queue detail (an instruction nobody reads is worthless), and
in the service-history modal."* Shipping the write side without the fulfiller read side would build exactly
the worthless instruction the doc names. So the lab, radiology, procedure and pharmacy queue details get the
read-only panel in this pass, deliberately crossing the portal boundary, and pass 6 audits those portals
otherwise.

### 3.5 F6 — the chronic amendment dialog, with the conversion stated

`AmendLineDialog` gains a chronic variant: duration in days, frequency in months, a coded reason, and a
preview of the re-allocation showing dispensed windows as fixed and future windows as recomputed. Three
refusals are rendered as sentences before the request rather than as 409s after it:

- a new total below what is already dispensed — *"That would un-dispense N packs already collected."*
- shortening below the chronic definition — the `ConvertToAcute` confirmation, worded as what it does to the
  patient's expectation, not as a flag.
- an amendment beyond the approved scope — says the script returns for authorisation (doc 46 §5).

`cancel-lines` reuses `withdrawOrder`'s existing partial-success rendering, which already reports
"three of five withdrawn" as an answer rather than an error.

### 3.6 F7 and F8 — the two "build or retire" questions, answered

**`medication_history`: keep it and build the writer.** The enum settles it — `SelfReported` and `External`
are facts about a patient that no query over Mersal's data can reconstruct, and F1 needs them. The encounter
workspace gains a **Current medications** section: add, mark stopped, with source and dates. Retiring the
table would mean deciding that Mersal never records what a patient is already taking, which is not a
tidy-up and is not this pass's call to make silently.

**`GET /{id}/dispensing`: decide by reading it, in the implementation.** If its projection is a superset of
what `queue`/`search` return, wire it and drop the duplication; if it is a subset, delete it and record why.
Either outcome is written down. What it must not stay is served, unreached and unexplained.

---

## 4. Gates

The pass is large — it is the first audit of the platform's biggest write surface — so it lands as ordered
gates, each independently green and reviewable.

| Gate | Content | Why here |
|---|---|---|
| **1** | F1: source the list, state coverage, `Unavailable` never `Ok` | Clinical safety, and it is a library change with a test suite that already exists |
| **2** | F7 writer: current-medications section | Gate 1's source; they are one feature split by layer |
| **3** | F2: addendum | Self-contained, high value |
| **4** | F3 + F4: the two result-access transitions | Self-contained |
| **5** | F5b then F5a: prescription notes server-side, then the panel across doctor and fulfiller views | Largest; server before client |
| **6** | F6: chronic amendment + cancel-lines | Depends on nothing above |
| **7** | F8 + design doc `50-the-prescribers-portal.md` + invariant registry | Closing |

---

## 5. Testing

**Gate 1 is the one that matters most**, and its test is the shape of the finding: a prescription written
against a beneficiary with an interacting active medication must produce an interaction finding, and the
same prescription with the source unavailable must produce `Unavailable` — **asserting the state, not the
absence of a crash**. A test that only checks "no exception" would pass today.

- `libs/clinical-validation`: the union source, the three coverage sentences, and `Unavailable ≠ Ok`.
- `services/pharmacy`: notes persist, class projection refuses the wrong reader, sensitivity inherited from
  the line, a note does not supersede the line or touch its authorisation.
- `services/emr`: the medication-history writer, and the addendum's 422 on empty content.
- `services/orders`: unchanged; its note suite already exists and must stay green.
- `apps/web/test/http-client-contract.test.ts`: **every new client method added to it.** Pass 4 built this
  because its absence was that pass's headline defect; a pass that adds a dozen mappings without extending
  it would rebuild the hole.
- Screen tests: the addendum flow, both result-access transitions, the notes panel in both roles, the
  chronic dialog's three refusals.
- axe over every new route × locale × theme, as the existing sweep does.

## 6. Risks

- **Gate 1 changes what a prescriber sees on a screen they trust.** More findings will appear where none
  did. That is the point, but it is a clinical-facing behaviour change and the design doc says so.
- **The union source costs a query per validation run**, which runs on every keystroke-debounced compose.
  It is a local read in pharmacy's own database plus one emr call; if it proves hot, it is cacheable per
  encounter — but not before it is measured.
- **F5b adds a table.** Expand-only, no backfill, idempotent under re-application like every migration here.
- **Crossing into the fulfiller portals** (§3.4) means pass 6 inherits screens this pass touched. Recorded
  so that reads as intent rather than as pass 6 finding something unexplained.
