# Phase 26 — Prescribing workspace & clinical validation (doctor side)

**Goal:** Replace the placeholder prescribe modal with a real prescribing workspace — drug search by **trade name or active ingredient**, multi-line prescriptions with dose/duration/quantity, a **Validate** pass that checks indication↔diagnosis, drug–drug interactions, allergies and benefit coverage, and a **Submit** that makes the prescription retrievable by the dispensing provider.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Design: [`../43-approval-engine-and-prescribing-support.md`](../43-approval-engine-and-prescribing-support.md)

> ⚠️ **Read ../43 §1 and §2 before writing code.** This puts automated advice in front of a doctor treating refugees. Two rules dominate:
> **(1) Clinical checks WARN; they never block.** Only benefit rules block. **(2) "Check unavailable" is NEVER rendered as "OK".**
> Today `pharmacy/Api/HttpClients.cs:71,84,103` swallows every transport error into "no alerts" — an outage currently renders as a clean bill of health. That is the most dangerous line in the prescribing path and this phase deletes it.
>
> **The free drug-interaction API assumption no longer holds.** NLM's RxNav Drug Interaction API was discontinued 2 Jan 2024; DrugBank's free checker retires March 2026. openFDA is live but serves US label *prose*, not severity-graded pairs. So interaction checking is **local**, against `masterdata.drug_interaction` (table exists, empty, no loader), behind an adapter seam. See ../43 §2.

## Skills to activate
> Superpowers: use the **brainstorming** skill before 26.1 (the combobox/validation UX has real alternatives worth surfacing), **writing-plans** for the migration sequencing, and **test-driven-development** for 26.3–26.4 — the validation engine is exactly the kind of pure logic that should be written test-first.
> Project skills: `mersal-platform-architect`, `refugee-healthcare-management` (always-on), `pbm-adjudication-engine`, `clinical-workflow-designer`, `healthcare-uiux-designer`, `healthcare-database-architect`.

## Context — read first
- [`../43-approval-engine-and-prescribing-support.md`](../43-approval-engine-and-prescribing-support.md) — **AUTHORITATIVE**: §0 (what exists), §1 (safety position), §2 (data sources), §5 (two-step), §6 (the workspace), §8 (invariants).
- **Existing code:** `services/pharmacy/{Api/Prescriptions.cs,Api/HttpClients.cs,Domain/PrescribingAlerts.cs,Domain/RxRouting.cs,Infrastructure/Migrations/0001_pharmacy.sql}`, `services/masterdata/{Api/Program.cs,Infrastructure/Migrations/0001_masterdata_schema.sql}`, `services/emr/{Api/ClinicalRecords.cs,Infrastructure/Migrations/0005_clinical.sql}`, `tools/masterdata-loader/**`, `apps/web/src/screens/DoctorEncounter.tsx` (`DiagnosisPicker` :766-966 is the typeahead pattern to follow; `PrescribeModal` :1359-1434 is what you are replacing), `apps/web/src/api/HttpApiClient.ts:883,1105-1128`.
- `docs/HANDOFF.md` gotchas. **Run DB-gated tests with `./dotnet.sh test --with-db`** — a plain `dotnet test` skips ~100 tests and reports green.

## THE INVARIANTS
1. Benefit rules may block; **clinical checks may only warn** (acknowledge + reason to proceed).
2. **Unavailable ≠ OK.** Five per-line states, four cues each.
3. Every finding carries **source + version + timestamp**, shown and stored.
4. Step 1 (this phase) is **advisory and untrusted**; phase 27's step 2 re-evaluates server-side.
5. No PHI leaves the platform for a clinical check without passing the existing DPIA gate.

---

## Prompts

### 26.1 — Ingest the drug list: `drug_indication` + the missing columns
```text
The drug↔ICD link DOES NOT EXIST anywhere in the platform. Build it.

SOURCE: "Master Lists/egyptian-drug-list_5.xlsx" (confirmed present; referenced by NO code today).
FIRST, INSPECT IT — do not assume columns. Print the sheet names, headers and 20 sample rows, and write
what you found into the loader README. Report the column mapping BEFORE writing the loader. If the sheet
carries no ICD/indication column, STOP and say so: the indication check cannot be built without it, and
inventing a mapping from drug name to ICD is not acceptable.

MIGRATION (masterdata):
- drug_indication: indication_id, drug_id -> masterdata.drug, icd_code varchar(10), is_primary bool,
  source varchar(64), source_release, + audit columns, soft-delete. Index (drug_id), (icd_code).
- Backfill masterdata.drug.name_ar and .strength — the loader never populates them today
  (Mappers.cs:30-41) and the combobox needs the Arabic name.
- Add masterdata.drug.trade_name if the sheet distinguishes trade name from `name`; otherwise document
  that `name` IS the trade name and `scientific_name` is the active ingredient.

LOADER (tools/masterdata-loader): add LoadDrugIndications following the existing LoadIcd/LoadCpt shape —
CSV or XLSX read, dry-run support, SHA256 provenance in the load report, idempotent upsert keyed on
(drug_code, icd_code). Validate every icd_code against masterdata.icd_code and REPORT unmatched rows
rather than silently dropping them; a drug whose indications all failed to match must be visible in the
report, because it will silently produce "not checked" forever otherwise.

ALSO: masterdata endpoints today are RequireAuthorization() with NO SCOPE (masterdata/Api/Program.cs:52).
Add `masterdata:read` and require it. Reference data is not secret, but an unscoped endpoint is an
unbounded one.

ACCEPTANCE: sheet inspected and mapping documented; drug_indication loaded with a provenance report;
unmatched ICDs reported not dropped; name_ar/strength populated; masterdata endpoints scoped.
TESTS: loader idempotency, unmatched-code reporting, dry-run makes no writes.
```

### 26.2 — Drug search API fit for a combobox
```text
GET /api/v1/drugs/search?q=&page=&pageSize= (scope masterdata:read)
- Searches BOTH trade name and active ingredient (scientific_name), plus Arabic name, case- and
  diacritic-insensitive. Add the indexes (trigram or equivalent) — a typeahead that table-scans a
  30k-row drug table is a typeahead nobody uses.
- Returns per row: drugId (uuid — REAL id, see 26.5), tradeName, tradeNameAr, activeIngredient,
  strength, form, priceEgp, atcCode, isBatchTracked-irrelevant, plus `hasIndicationData` bool so the UI
  can distinguish "no indication match" from "no indication data".
- Ranking: exact trade-name prefix > ingredient prefix > contains. Cap pageSize at 50.
ACCEPTANCE: searching "augmentin" and searching "amoxicillin" both return the Augmentin rows; Arabic
query works; p95 < 300ms on the full loaded list.
TESTS: both search axes, Arabic, ranking order, cap enforcement, index presence assertion.
```

### 26.3 — The validation engine (pure domain, test-first)
```text
Read ../43 §1, §2, §6. Use the superpowers test-driven-development skill: write the fact/outcome tests
first — this is pure logic and belongs in Domain with no I/O.

New: services/pharmacy/Domain/PrescriptionValidation.cs (or a shared lib if phase 27 will reuse it —
prefer libs/clinical-validation so phase 27's step 2 uses THE SAME code, not a copy).

CHECK KINDS, each returning Ok | Warning | Blocked | NotChecked | Unavailable (five states, never four):
1. INDICATION ↔ DIAGNOSIS: does any encounter diagnosis ICD appear in this drug's drug_indication set?
   match -> Ok; no match -> Warning "not a listed indication" (off-label is legitimate and common —
   NEVER Blocked); drug has no indication rows -> NotChecked with the reason; masterdata unreachable ->
   Unavailable.
2. DRUG-DRUG INTERACTION: cross-line, every pair in the prescription AND against the member's active
   medications if available. Source is the LOCAL masterdata.drug_interaction table (../43 §2) — do NOT
   call an external interaction API. Severity from the table; Warning at any severity; the UI decides
   emphasis. Empty table -> NotChecked ("Mersal interaction list contains 0 pairs"), NOT Ok.
3. ALLERGY: existing masterdata /allergies/check-by-ids + emr allergies. Same five states.
4. DOSE/DURATION: evaluate ONLY against structured dosing rules where one exists for the drug
   (max daily dose, duration ceiling, paediatric weight band, renal flag). No rule -> NotChecked.
   Do NOT attempt to derive a dose from label prose (../43 §2).
5. BENEFIT PRE-CHECK: formulary/exclusion/limit/pre-auth — a SEAM in this phase (interface +
   always-NotChecked implementation), implemented for real in phase 27. Wire the seam now so phase 27
   swaps an implementation rather than restructuring the engine.

EVERY finding carries: checkKind, state, severity?, message (EN+AR), sourceName, sourceVersion,
checkedAt, and the drug/line it belongs to.

DELETE THE SWALLOW: pharmacy/Api/HttpClients.cs:71,84,103 currently catch HttpRequestException and
return no alerts. Replace with an explicit Unavailable finding. Add the test that kills the dependency
and asserts the result is Unavailable and NOT Ok — this test is the whole point of the phase, register
it in docs/quality/invariant-registry.yaml (phase 24 Gate 2).
ACCEPTANCE: all five kinds return all five states correctly; a dead dependency yields Unavailable
everywhere; no check can return Blocked except the benefit seam.
TESTS: table-driven per kind × state; the dependency-down test; cross-line pair generation (n lines ->
n(n-1)/2 pairs, deduped); provenance present on every finding.
```

### 26.4 — Prescription schema + validate/submit API
```text
MIGRATION (pharmacy):
- prescription_line: ADD duration_days int NULL (and/or duration_text) — there is dose/route/frequency/
  quantity today but NO duration, and duration is what makes a daily-dose ceiling checkable.
- prescription: ADD primary_icd_code varchar(10) NULL + a diagnosis snapshot (jsonb of the encounter's
  ICDs at prescribing time). The prescription has NO diagnosis link today and the check needs one;
  snapshot rather than FK, because a later diagnosis edit must not rewrite what was checked.
- prescription_validation: validation_id, prescription_id NULL (draft validations have none),
  encounter_id, ran_at, ran_by, findings jsonb, engine_version, overall_state. Append-only.
- prescription_line_override: line_id, finding_ref, reason varchar(300) NOT NULL, acknowledged_by,
  acknowledged_at. Append-only. This is the clinician's recorded justification for proceeding.

API (scope rx:write, treating-relationship gate as today):
- POST /api/v1/prescriptions/validate  { encounterId, lines[] }  -> findings, no persistence of a draft
  prescription. Idempotency-Key NOT required (it is a read-shaped operation) but rate-limit it.
- POST /api/v1/prescriptions (existing, extend): accepts lines with duration, the diagnosis snapshot,
  and acknowledgements[] for any Warning being overridden. REJECT 422 if an unacknowledged Warning is
  present — the acknowledgement, not the warning, is what gates submit.
- SERVER RE-RUNS VALIDATION ON SUBMIT. The client's findings are display state only. A submitted
  payload claiming everything is fine must not be believed (../43 §5). Add the test that forges a
  clean client payload against a drug with a known interaction and asserts the server still records it.
ACCEPTANCE: duration persisted; diagnosis snapshot stored; validation runs recorded; unacknowledged
warning blocks submit; forged client verdict is ignored.
TESTS: the forged-payload test (registry-pinned), acknowledgement flow, snapshot immutability under a
later diagnosis edit.
```

### 26.5 — The prescribing workspace UI
```text
Read ../43 §6 and ../0B (§10c paired actions, four-cue rule), ../21. Replace PrescribeModal
(DoctorEncounter.tsx:1359-1434). Follow DiagnosisPicker (:766-966) — it is already a correct debounced
typeahead in this codebase; do not invent a second pattern.

FIX THE BROKEN PATH FIRST: the modal hard-codes J01CA04/"Amoxicillin 500mg" (:1363-1366) and the client
sends `drugId: req.drug.code` — the ATC STRING where the API expects a Guid (HttpApiClient.ts:883). It
cannot work against real data. The combobox must carry the real uuid.

COMBOBOX (ARIA 1.2 combobox pattern — real roles/aria-activedescendant, not a styled div):
- One field, searches trade name AND active ingredient, debounced ~250ms, min 2 chars.
- Option layout: trade name as the title line; active ingredient + strength + price on a second,
  smaller, muted line. This is a safety feature, not decoration — two trade names holding the same
  molecule is the commonest duplication, and the ingredient must be visible at the moment of choosing.
- Keyboard: up/down/enter/escape, aria-live result count, works in AR with RTL, no mouse required.

LINE EDITOR: drug · dose · duration · quantity · status. Add/remove lines freely; at least one required.
STATUS PER LINE — FIVE states, FOUR cues each (hue + icon + shape + word), never colour alone:
  Not checked · OK · Warning · Blocked · **Check unavailable**
"Check unavailable" must be visually distinct from OK and from Warning. A greyed tick meaning "we could
not check" is the failure this phase exists to prevent.

ACTIONS: [Validate] then [Submit]. Submit disabled until a validate run exists for the current line set
(mutating a line invalidates the run). Each Warning expands to show finding text + SOURCE + VERSION +
time, with an "Acknowledge and give reason" control; submit stays disabled while any Warning is
unacknowledged. Blocked lines cannot be submitted at all.
DIAGNOSIS: pass the encounter's staged diagnoses into the modal (it receives only encounterId today).
If none recorded, the indication check shows "no diagnosis recorded" — NOT OK.

ACCEPTANCE
- Given "augmentin" or "amoxicillin", Then the same product is findable by either, with ingredient and
  price visible before selection.
- Given masterdata is down, Then every line shows "Check unavailable" and Submit remains possible only
  with an acknowledgement — never a silent OK.
- Given a Warning, Then submit is blocked until acknowledged with a reason, and the reason is stored.
- Given AR locale, Then the combobox, options and statuses render RTL and axe passes.
TESTS: combobox keyboard + a11y (axe EN+AR against POPULATED fixtures — add DevApiClient fixtures from
the start), five-state rendering, unavailable≠OK assertion on the rendered DOM, invalidate-on-edit,
acknowledgement gating, the real-uuid regression test.
```

### 26.6 — Provider visibility (card number) — build the missing plumbing
```text
Read ../43 §7. "Visible to the provider by card number" does not work today and three things block it:
(a) patient.beneficiary.card_number exists but NO search filter reaches it and IdentifierType has no
    CardNumber member (patient/Domain/Entities.cs:8);
(b) GET /beneficiaries/resolve — which pharmacy ALREADY CALLS (pharmacy/Api/HttpClients.cs:143-168) —
    DOES NOT EXIST; the client swallows the 404 and returns empty, so those search arms silently
    return nothing today;
(c) any pharmacist with pharmacy:read browses the entire network queue with no row-level binding
    (pharmacy/Api/DispensingGate.cs:8-11).

DO:
- Add CardNumber to IdentifierType and make it searchable (indexed, unique among live rows as the
  column already is).
- IMPLEMENT GET /api/v1/beneficiaries/resolve properly, or delete the caller. A client calling a
  non-existent endpoint and failing silently is worse than no feature.
- Card-number retrieval returns the MINIMUM-NECESSARY DISPENSING VIEW: lines, quantities, status,
  prescriber, expiry. NO diagnosis, NO clinical notes, NO encounter content. Prove with a projection
  test over the serialized payload.
- REQUIRE A SECOND IDENTIFIER (../43 D5) — reuse the call-centre ≥2-identifier-type rule which is
  already built and tested (phase 15). A card is shared, photographed and reused; it is not an
  authenticator.
- Audit every card-number retrieval as a PHI read with the identifiers used (types only, never values —
  the phase-15 privacy rule).
ACCEPTANCE: resolve endpoint exists and works; card-number lookup needs two identifiers; the returned
payload contains no clinical field; every retrieval audited.
TESTS: projection test on the serialized payload, single-identifier rejection, audit assertion.
```

### 26.7 — Docs, routes, registry
```text
- ../22 gains drug_indication, duration_days, prescription_validation, prescription_line_override,
  the diagnosis snapshot and CardNumber; ../23 gains the validation states on the prescription lifecycle;
  ../17 gains the new endpoints; ../11 gains masterdata:read; 00-README-INDEX + README gain doc 43.
- Kong routes for /drugs/search, /prescriptions/validate, /beneficiaries/resolve; route-coverage guard green.
- BUILD-STATUS gains 26.1-26.7.
- Register in docs/quality/invariant-registry.yaml: "a failed clinical check renders Unavailable, never
  OK", "server re-validates and ignores client verdicts", "card-number view contains no clinical field".
- ADR-0026: local-first interaction checking, the discontinued-API context, openFDA as reference text
  only, and why clinical checks warn rather than block.
ACCEPTANCE: docs true; routes reachable; registry entries have named tests; ADR merged.
```

---

## Guardrails
- **Clinical checks never block. Benefit rules block.** If you find yourself writing `Blocked` in a clinical check, stop.
- **No check may return OK when its data source failed.** Delete every silent catch; there are three today.
- **No external clinical API is called with PHI** in this phase. Interactions are local; openFDA (if used at all) is mirrored reference text, never live per-patient (../43 D4).
- **Off-label is legitimate** — an indication mismatch is a Warning, forever.
- The combobox must carry a **real drug uuid**; the ATC-string defect is a regression test, not a footnote.
- Every finding stores provenance; every override stores a reason.
- Full suite green after each sub-prompt (`./dotnet.sh test HbmpPlatform.sln -c Release --with-db` + `pnpm -r test`).

## Done when
- [ ] `egyptian-drug-list_5.xlsx` inspected, mapping documented, `drug_indication` loaded with provenance and an unmatched-code report; `name_ar`/`strength` populated; masterdata endpoints scoped.
- [ ] Drug search finds a product by trade name **or** active ingredient, in AR and EN, fast, returning real uuids.
- [ ] Validation engine returns **five** states across five check kinds, each with provenance; a dead dependency yields **Unavailable**, proven by test and pinned in the invariant registry.
- [ ] Duration persisted; diagnosis snapshotted; validation runs and overrides recorded append-only; **a forged client verdict is ignored by the server**, proven by test.
- [ ] Prescribe workspace: ARIA combobox showing ingredient + price under the trade name, multi-line, five-state per-line status with four cues, Validate→Submit gating, acknowledgement with reason.
- [ ] Card-number retrieval works, requires a second identifier, returns no clinical field, and is audited; the phantom `resolve` endpoint is implemented or its caller deleted.
- [ ] Docs, routes, ADR-0026 and registry entries complete; axe clean EN+AR on populated fixtures.
