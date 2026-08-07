# 43 — Approval Engine, Benefit Lists & Prescribing Decision Support

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [38-policy-member-administration.md](38-policy-member-administration.md) · [36-claims-management.md](36-claims-management.md) · [40-user-access-model.md](40-user-access-model.md) · [11-permission-matrix.md](11-permission-matrix.md) · [20-compliance-checklist.md](20-compliance-checklist.md)
> Build prompts: [phase-26-prescribing-workspace.md](claude-code-prompts/phase-26-prescribing-workspace.md) (doctor side, first) · [phase-27-approval-engine.md](claude-code-prompts/phase-27-approval-engine.md) (engine, lists, rules, supervisor)

**What this adds.** A prescribing workspace where the doctor searches real Egyptian drugs by trade name *or* active ingredient, builds a multi-line prescription, and gets it checked before it leaves the room — then an **approval engine** that evaluates benefit rules, formularies, exclusions and escalations authored by an **Approval Supervisor**, at two points: when the order is written, and again when it is adjudicated.

---

## 0. What exists today (verified in code — do not rebuild, and do not assume)

| Thing | State | Evidence |
|---|---|---|
| Approvals service: worklist, assign, approve/partial/reject/request-info, break-glass, SLA, append-only decision ledger | **Built and solid** | `approvals/Api/{Worklist,Decisions,BreakGlass}.cs`; `0001_approvals.sql:46-78` |
| **Rules engine in approvals** | **DOES NOT EXIST.** Adjudication is 100% human. `DecisionRules.cs` is 32 lines: blank-check, partial-scope subset, TAT, SLA-breach | `approvals/Domain/DecisionRules.cs` |
| **Rejection reason codes** | **DO NOT EXIST** — free-text rationale only | `Decisions.cs:195-200` |
| **Formulary / drug list / exclusion list attachable to a policy** | **DOES NOT EXIST** | no `formulary`/`drug_list`/`benefit_list` table anywhere |
| `benefit_rule.exclusions jsonb` | Exists, parsed into a DTO, **never evaluated against anything** | `0005_pas_plan.sql:118`; `CoverageDetail.cs:163` |
| "Formulary service" | A stand-in: *approved alternatives* = any drug sharing the ATC-5 code, `.Take(8)`. **No policy is consulted** | `masterdata/Api/Program.cs:213-226` |
| `masterdata.drug` (code, name, scientific_name, manufacturer, form, atc_code, price_egp) | **Loaded** from `Raw Files/Egyptian Drugs - ATC Classified.csv`. `name_ar` and `strength` never populated | `0001_masterdata_schema.sql:41-53`; `Mappers.cs:30-41` |
| `masterdata.drug_interaction` (drug_a, drug_b, severity, description) | **Table exists, EMPTY, no loader** | `0001_masterdata_schema.sql:56-64` |
| **Drug ↔ ICD link** | **DOES NOT EXIST** anywhere | — |
| Prescription has a diagnosis / ICD | **NO column, NO parameter**; the prescribe modal never receives the encounter's diagnoses | `0001_pharmacy.sql:15-46`; `DoctorEncounter.tsx:1359-1434` |
| Doctor's Prescribe modal | 4 plain text inputs, **hard-coded defaults** (`J01CA04`, "Amoxicillin 500mg"), **no autocomplete**, and it sends the ATC *string* where the API expects a `Guid drugId` — **the path cannot work against real data** | `DoctorEncounter.tsx:1363-1366,1414-1422`; `HttpApiClient.ts:883` |
| `searchDrugs` API client method | Exists, used only by the pharmacist substitution screen — not by prescribing | `HttpApiClient.ts:1105-1116` |
| Prescribing screening (interaction/allergy) | Calls masterdata, **swallows every transport error into "no alerts"**, never blocks | `pharmacy/Api/HttpClients.cs:71,84,103` |
| Retry / circuit breaker / resilience anywhere in the platform | **DOES NOT EXIST** — no Polly, no resilience handler, bare `AddHttpClient` | repo-wide |
| DPIA gate (CI + runtime + DB constraint) for external partners | **Built** — reuse it | `tools/ci/check-integration-dpia.py`; `interop/…/0002_integration.sql:18-21` |
| `approval_supervisor` role | **DOES NOT EXIST** | `0001_identity.sql:144-149` |
| Pharmacy → prescription binding | **None at row level.** Any pharmacist with `pharmacy:read` browses the whole network queue | `pharmacy/Api/DispensingGate.cs:8-11` |
| Look-up **by card number** | **DOES NOT WORK.** `patient.beneficiary.card_number` exists but no search filter reaches it; `IdentifierType` has no `CardNumber`; the `beneficiaries/resolve` endpoint the pharmacy calls **does not exist** and fails silently to an empty list | `patient/Api/Program.cs:192-220`; `patient/Domain/Entities.cs:8`; `pharmacy/Api/HttpClients.cs:143-168` |
| masterdata endpoints | `RequireAuthorization()` with **no scope at all** | `masterdata/Api/Program.cs:52` |

## 1. The safety position — this is the design's spine

This module puts automated advice in front of a prescriber treating refugees. Two categories of rule behave differently, and conflating them is the failure mode:

| | **Benefit rules** (administrative) | **Clinical checks** (safety) |
|---|---|---|
| Examples | formulary, exclusion, escalation, limits, pre-auth thresholds, cost | drug–drug interaction, dose, indication ↔ diagnosis, allergy |
| Source | Mersal's own data, authored by the supervisor | external/curated clinical data |
| Determinism | total — same inputs, same answer, explainable line by line | probabilistic, incomplete, source-dependent |
| May it **block**? | **Yes.** "Not covered" is a factual statement about a policy | **No.** It warns, requires acknowledgement, and records an override reason |

**Three rules that follow, and are non-negotiable:**

1. **A clinical check never silently passes.** "No interactions found" and "the interaction service was unreachable" are different answers and must look different — the same three-state discipline as [doc 39](39-patient-profile.md) (Visible / Restricted / **Unavailable**). Today the screener swallows every transport error into "no alerts" (`pharmacy/Api/HttpClients.cs:71`). That is the single most dangerous line in the current prescribing path: an outage renders as a clean bill of health.
2. **Every advisory carries its provenance** — source name, dataset version, retrieval timestamp — displayed to the prescriber and stored with the prescription. A warning you cannot attribute is a warning a clinician is right to ignore.
3. **Overrides are expected and recorded, not prevented.** The prescriber may proceed past any clinical warning with a reason; that reason is part of the record and visible to the approver. Blocking a doctor on automated advice of uncertain provenance would be the greater harm.

## 2. The data-source problem (read before promising an interaction check)

The request assumed a *free* drug-interaction API. As of 2026 that assumption no longer holds:

- **NLM's RxNav Drug Interaction API was discontinued on 2 January 2024**, with no replacement and no migration path. The rest of RxNav (RxNorm normalisation, `findRxcuiByString`, approximate match) **is still live and still free**.
- **DrugBank retires its free interaction checker in March 2026.** Its commercial API continues.
- **openFDA is free and live**, but it serves *US structured product labels*. Interactions appear as **free-text prose** in a label section, not as severity-graded pairs; `indications_and_usage` and `dosage_and_administration` are likewise prose. There is no field that answers "is this dose right for this diagnosis".

Two further mismatches specific to Mersal: openFDA and RxNorm are **US-centric**, so Egyptian trade names will not resolve — mapping must go **through the active ingredient**, which the Egyptian drug list carries. And sending a beneficiary's diagnosis plus medication list to a third-party API is a **PHI disclosure to an external processor**, which the platform's own DPIA gate governs ([doc 20](20-compliance-checklist.md); `interop/…/0002_integration.sql:18-21`).

### Decision

**`masterdata.drug_interaction` — which already exists and is empty — becomes the system of record.** Interaction checking is evaluated **locally**, against a table Mersal controls, behind an adapter seam:

| Source | Role | Status |
|---|---|---|
| Curated internal table | **System of record.** Populated by pharmacist review, seeded from any licensed dataset Mersal obtains | Build now |
| Licensed commercial dataset (DrugBank / Lexicomp / FDB / Medi-Span) | Optional import into the same table | Procurement decision, D3 |
| **RxNorm** (free, live) | Ingredient normalisation only — Egyptian trade name → active ingredient → RxCUI, to make an imported dataset mappable | Build the mapper |
| **openFDA** (free, live) | **Reference text only**, shown labelled as *"US FDA label — reference, not a coverage or dosing decision"*. Never parsed into a severity, never used to auto-block | Build read-only, cached |

Why local: a safety check that depends on an unlicensed free endpoint is a safety check that disappears without notice — which is exactly what happened twice in twenty-four months. It also keeps PHI inside the platform, so no DPIA is required for the core check.

**Dose checking is scoped honestly.** Automated "is this dose correct for this diagnosis" cannot be derived from label prose. What is defensible: **structured dosing rules for a curated, high-risk subset**, authored by the supervisor/pharmacist in the same rule engine as everything else — maximum daily dose, paediatric weight-band, renal adjustment, duration ceiling. Outside that subset the system says *"no dosing rule configured"* — not silence, and never an implied endorsement.

## 3. Benefit lists — one concept, three kinds

Formulary, exclusion and escalation are the same shape: a **named, versioned list of items, attached to something, with a precedence**. Building three tables produces three near-identical implementations that drift, so:

- **`benefit_list`** — `kind ∈ {Formulary, Exclusion, Escalation}`, name (EN/AR), owner, status `Draft|Active|Retired`, **effective-dated and immutable once Active** (new version, never edit — the [doc 38](38-policy-member-administration.md) plan-version discipline).
- **`benefit_list_item`** — `item_type ∈ {Drug, AtcClass, IcdCode, ServiceCode}` + value + optional note. ATC-class entries let "all systemic quinolones" be one row instead of ninety.
- **`benefit_list_attachment`** — attaches a list version to a **payer, policy, plan, or member group**, with `valid_from/valid_until` and a precedence order. This is where *"the UNHCR formulary applies to this group"* is expressed, and it is edited from the policy configuration portal ([doc 38](38-policy-member-administration.md)) as well as the supervisor portal — one model, two doors.

**Precedence must be explicit or the engine is unexplainable.** The resolution order, most specific wins:

`member group → policy → plan → payer`

and within a level: **Exclusion beats Formulary.** A drug on both is *excluded* — the safe reading of a contradiction. **Escalation never blocks**; it routes the request to a named queue or reviewer tier. Every evaluation records which list version at which level produced the outcome.

## 4. The rules engine

Supervisor-authored rules, evaluated by the engine. What makes this safe rather than a liability:

- **A constrained condition builder, not code.** Conditions compose over a fixed vocabulary of facts — drug, ATC, ICD, service code, cost, member age, plan, group, network tier, cumulative utilisation, list membership — with a fixed operator set. No free expression evaluation, ever: an editable expression that runs server-side is a remote-code-execution surface authored by a non-engineer.
- **Actions are a closed set:** `Allow`, `Deny(reasonCode)`, `RequirePreauth`, `Escalate(queue)`, `RequireDocument(kind)`, `CapQuantity(n)`, `WarnOnly(message)`.
- **Reason codes become a real vocabulary** — approvals has none today, only free text. Every Deny carries a code that the member-facing explanation, the claims denial and the analytics all share.
- **Draft → Simulate → Peer review → Active.** A rule that can deny care is dual-controlled (the existing SoD engine and peer-review flag already do this for high-tier grants).
- **Simulation before activation is mandatory**, not optional: replay the rule against the last N days of real authorizations and show what *would* have changed. A rule that would have denied 40% of last month's requests must never reach production unseen. This is the single highest-value feature in the engine.
- **Versioned and effective-dated**; every decision stores the **rule version ids that fired**. Six months later, "why was this denied?" has an answer that survives the rule being changed since.
- **Deterministic ordering** — rules evaluate in explicit priority order; first terminal action wins; conflicts are detected at authoring time, not discovered in production.

## 5. Two-step validation

**Step 1 — at the point of writing** (doctor, in the encounter): clinical advisories + a benefit **pre-check**. Result is *indicative*: "this will need pre-authorisation", "this drug is outside the UNHCR formulary — alternatives: …". It is fast, it is helpful, and it changes what the doctor prescribes.

**Step 2 — authoritative** (on submission, and again at adjudication): the same engine, server-side, over the current member state, current lists and current rule versions.

**The rule that matters:** *step 1's verdict is never an input to step 2.* The client's validation result is untrusted data; step 2 re-evaluates from scratch. Otherwise a crafted submission carrying `"validated": true` walks past the entire engine — the same class of hole as trusting a client-filtered payload ([doc 39](39-patient-profile.md) §1). Step 1 exists to inform a human, not to authorise.

Eligibility, coverage limits and cost-share can all change between the two steps; a divergence between step 1 and step 2 is normal and must be shown plainly to the prescriber rather than treated as an error.

## 6. Prescribing workspace (doctor portal → Orders → Prescribe)

**Drug search combobox.** One field searching **both trade name and active ingredient**, debounced, Arabic- and English-aware, keyboard-navigable (ARIA 1.2 combobox — a real one, not a styled div). Each option renders:

```
Augmentin 1g  ·  14 tablets                    ← trade name, title weight
amoxicillin + clavulanic acid · 210 EGP        ← ingredient + price, smaller, muted
```

The ingredient line is what makes this safe: two boxes of different trade names holding the same molecule is the commonest prescribing duplication, and showing the ingredient at selection time is the cheapest possible defence.

**Line fields:** drug · dose · **duration** · quantity · status icon. Multiple lines per prescription, add/remove freely. `duration` is a new field — the current schema has dose/route/frequency/quantity but no duration, and duration is what makes a daily-dose ceiling checkable.

**Status icon per line — four states, four cues** (hue + icon + shape + word), never a bare colour: `Not yet checked` · `OK` · `Warning (acknowledge to proceed)` · `Blocked (benefit rule)` · `Check unavailable`. The last one is mandatory and must never render as OK.

**Validate** runs step 1 across all lines together (interactions are cross-line by nature); **Submit** is enabled only after a validate run, and any unacknowledged warning requires a reason before submit.

**A diagnosis must exist on the encounter before validation can be meaningful.** The ICD-consistency check has nothing to compare against otherwise — so if no diagnosis is recorded, the check reports *"no diagnosis recorded"* rather than passing. The prescribe modal must receive the encounter's staged diagnoses, which today it does not (`DoctorEncounter.tsx:1359-1434` gets only `encounterId`).

### The drug↔ICD consistency check

There is **no drug↔ICD link in the platform today**. `Master Lists/egyptian-drug-list_5.xlsx` reportedly carries indication/ICD data per drug; the loader must ingest it into a new `drug_indication` table (`drug_id | icd_code | source`). The check is then: *does at least one recorded encounter diagnosis appear in this drug's indication set?* Outcomes: match → OK; no match → **advisory** "not a listed indication" (off-label prescribing is legitimate and common — this can never block); **no indication data for this drug → "not checked", not OK.**

## 7. Provider visibility — the card-number gap

The request is that a submitted prescription be visible to the provider **by card number**. Three things must be built or fixed first, because none of them work today:

1. **Card number is not searchable.** The column exists on `beneficiary` and is unique among live rows, but patient search only queries the `identifier` child table, and `IdentifierType` has no `CardNumber` member. Either add it as a first-class identifier type or add an explicit lookup — and index it.
2. **The resolver endpoint the pharmacy already calls does not exist.** `GET /beneficiaries/resolve?policyNo=&passport=&memberNo=` has no implementation; the client swallows the 404 and returns an empty list, so those search arms silently return nothing.
3. **A card number alone is a weak key.** It is printed on a card that gets shared, photographed and reused — the exact fraud the photo on the patient profile was meant to deter ([doc 39](39-patient-profile.md) §5). Retrieval by card number therefore returns the **minimum necessary dispensing view** (lines, quantities, status — no diagnosis, no clinical notes) and either requires a second identifier, or is bound to the dispensing provider and audited as a PHI read. Today *any* pharmacist sees the entire network queue with no row-level binding at all (`DispensingGate.cs:8-11`); that should not be widened further without a decision.

## 8. Invariants

1. **Benefit rules may block; clinical checks may only warn.** Never the reverse.
2. **"Unavailable" is never rendered as "OK"** — for any check, at any step.
3. Every advisory and every decision records **source + version**; every rule decision records the **rule version ids that fired**.
4. **Step 1 is advisory and untrusted**; step 2 re-evaluates server-side and is authoritative.
5. **Lists and rules are versioned, effective-dated and immutable once Active**; changes create versions.
6. **Deny requires a reason code** from a controlled vocabulary — never free text alone.
7. **No rule may be activated without a simulation run** recorded against it.
8. Precedence is explicit and deterministic: group → policy → plan → payer; **exclusion beats formulary**; escalation never blocks.
9. The engine **owns no clinical data** and composes under the caller's authority; it never becomes a second route to PHI.
10. Every list, rule, attachment, validation run, override and decision is audited; no hard deletes.

## 9. Decisions needed

| # | Question | Recommendation |
|---|---|---|
| D1 | Do clinical warnings ever hard-block? | **No.** Warn + acknowledge + record reason. Blocking on unlicensed data would be the greater harm |
| D2 | Does the approval supervisor inherit `medical_approval`'s PHI reach? | **Yes**, plus list/rule authoring — but **rule authoring must not require PHI access**; a policy analyst should be able to author without reading records. Split the scopes so the two can separate later |
| D3 | Licence a commercial interaction dataset, or curate internally? | **Curate internally now, adapter seam for a licensed import.** Internal-only means partial coverage — say so in the UI ("checked against Mersal's interaction list, N pairs") rather than implying completeness |
| D4 | Is openFDA called live, or mirrored? | **Mirror**, cached, refreshed on a schedule. No PHI leaves the platform, no availability dependency in a clinical path, and it survives the API changing |
| D5 | Card-number retrieval: second identifier, or provider binding? | **Both eventually; a second identifier first** (the call-centre ≥2-identifier rule already exists and is proven) |
| D6 | Who may author a rule that denies care? | Supervisor authors; **a second person activates**. Dual control on anything with a `Deny` action |

## 10. Acceptance criteria

- [ ] Drug combobox searches trade name **and** active ingredient, renders ingredient + price beneath the trade name, is a real ARIA combobox, works in AR and EN, and returns real `drugId` uuids (fixing the ATC-string-as-Guid defect).
- [ ] Prescription lines carry dose, **duration**, quantity, and a per-line status with **five** distinct states including **Unavailable**.
- [ ] Validate runs cross-line interaction, indication↔diagnosis, allergy and benefit pre-check; provenance shown per finding; overrides require a reason and are stored.
- [ ] **A transport failure to any check renders "unavailable", never "OK"** — proved by a test that kills the dependency and asserts the rendered state and the stored result.
- [ ] `drug_indication` loaded from `egyptian-drug-list_5.xlsx`; no-indication-data reports "not checked".
- [ ] `benefit_list` (3 kinds) + items + attachments, versioned and effective-dated; attachable to payer/policy/plan/group from both portals; precedence resolved deterministically with exclusion beating formulary; a UNHCR formulary applied to a group demonstrably changes an outcome.
- [ ] Rule engine with a closed fact/operator/action vocabulary, reason-code vocabulary, versioning, and **mandatory simulation** before activation; decisions record the rule versions that fired.
- [ ] Step 2 re-evaluates server-side and **ignores any client-supplied validation verdict** — proved by a test that submits a forged "validated" payload against a denying rule and gets denied.
- [ ] `approval_supervisor` role: `medical_approval`'s scopes plus list/rule authoring, with authoring scopes separable from PHI scopes; dual control on deny-capable rules.
- [ ] Card-number retrieval returns a minimum-necessary dispensing view, requires a second identifier, and is audited.
- [ ] Bilingual AR/EN, WCAG 2.2 AA, axe clean against **populated** fixtures; every mutation audited; all pre-existing suites green.

---

### Cross-references
Policy/plan/group model: [38](38-policy-member-administration.md) · Claims denial codes: [36](36-claims-management.md) · Three-state rendering & untrusted-client rule: [39](39-patient-profile.md) · Authority vs reach: [40](40-user-access-model.md) · DPIA: [20](20-compliance-checklist.md) · Build: [phase-26](claude-code-prompts/phase-26-prescribing-workspace.md), [phase-27](claude-code-prompts/phase-27-approval-engine.md)
