# Phase 6 — Pharmacy Dispensing (Partial, Batch/Expiry, Substitution)

**Goal:** Give pharmacists the dispensing slice on top of phase-4 prescriptions: a **search** that surfaces only dispensable prescriptions (pharmacies cannot see investigation results), **partial dispensing** with batch/expiry that is atomic, idempotent, and leaves the remainder available, and a **substitution + out-of-stock** workflow. Release **R3**. Mirrors phase 5's consume/no-reuse pattern applied to medications.

Back to master list: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> Root `CLAUDE.md` already defines stack, conventions, security, audit, testing, and Definition of Done. This file adds phase-6 scope only.

---

## Skills to activate
> Activate `pbm-adjudication-engine`, `clinical-workflow-designer` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [`../07-functional-requirements.md`](../07-functional-requirements.md) — pharmacy/dispensing requirements.
- [`../22-data-dictionary.md`](../22-data-dictionary.md) — §8.1 `prescription`, §8.2 `prescription_line`, **§8.3 `dispense_event` (append-only)**. The `idempotency_key` UNIQUE constraint and immutable rows are authoritative. (§8.3 has `batch_no`; add an `expiry_date` column to capture lot expiry — note the extension in the migration.)
- [`../23-state-machines.md`](../23-state-machines.md) — §3 Prescription lifecycle and **"Pharmacy-specific guards"** (partial dispensing, substitution, out-of-stock, reject rule).
- [`../24-sequence-diagrams.md`](../24-sequence-diagrams.md) — partial-dispense sequence.
- [`../32-user-stories.md`](../32-user-stories.md) — US-050, US-051, US-052.
- Reference: [`../11-permission-matrix.md`](../11-permission-matrix.md) (min-necessary: pharmacies ≠ investigation results), masterdata-service (Drug/ATC, approved alternatives).

---

## THE INVARIANT (read before writing 6.2)

Prescription-line dispensing is **ATOMIC, IDEMPOTENT, and DUPLICATE-PROOF** — the same design as the order-line consume in phase 5, applied to `dispense_event`:

1. **Append-only `dispense_event`** per dispense (../22 §8.3), immutable, with `UNIQUE(idempotency_key)`.
2. **Optimistic concurrency (version)** on `prescription_line`: the dispense UPDATE is guarded `WHERE prescription_line_id=@id AND version=@version`; 0 rows ⇒ lost race ⇒ retry/return winner. A CHECK/partial index guarantees cumulative `quantity_dispensed` never exceeds `quantity_prescribed`.
3. **Idempotency-Key** required on the dispense endpoint; replay returns the prior dispense_event, no new row, no state change.

Semantics: dispense qty ≤ remaining → line `PartiallyDispensed`, prescription `PartiallyDispensed`, remainder stays **available** for a later visit. Full dispense → `Dispensed`. A used quantity can never be reclaimed (no-reuse). All in one transaction; `dispense_event` rows append-only; history via `audit_event`.

---

## Prompts

### 6.1 — Pharmacy search for dispensable prescriptions

```text
Extend pharmacy-service with a PHARMACIST-FACING search for dispensable prescriptions. .NET 8, REST /api/v1 + OpenAPI 3.1.

READ FIRST: ../22-data-dictionary.md §8, ../23-state-machines.md §3 (reject rule), ../11-permission-matrix.md, ../32-user-stories.md US-050.

Endpoint:
- GET /prescriptions/search?rxNo=... | patientIdentifier=... | policyNo=... | passport=... | memberNo=... — search by Rx number, Patient identifier, Policy, Passport, or Member number.

Behaviour & authorization (min-necessary is the core of this prompt):
- Return ONLY dispensable prescriptions: status Approved / PartiallyDispensed (i.e., approved and not fully dispensed / not expired / not cancelled). Show lines with remaining quantities (quantity_prescribed − quantity_dispensed).
- REJECT rule (../23 §3): opening/dispensing an Expired, Cancelled, Rejected, or fully Dispensed prescription is rejected with a clear reason (problem+json) and the attempt is audited.
- A pharmacist MUST NOT see investigation results or any lab/imaging data — this service does not expose them and cross-service reads are denied. Field-level projection returns only dispensing-relevant fields (drug, dose/route/frequency, remaining qty, patient identifier), never diagnoses/notes.
- Enforce at policy engine (scope pharmacy:read / rx:read) AND row/field level. PHI reads audited.

Acceptance criteria (US-050):
- Given a valid search, When it matches a dispensable Rx, Then I see its lines and remaining quantities and cannot see investigation results.
- Given an expired or completed Rx, When I open it, Then dispensing is rejected with the reason.

Tests: integration (search by each identifier type; only dispensable prescriptions returned; expired/completed rejected), AUTHORIZATION test proving a pharmacist cannot retrieve investigation orders/results (403/empty, attempt audited).
```

### 6.2 — Partial dispensing with batch/expiry (atomic + idempotent)

```text
Implement PARTIAL DISPENSING in pharmacy-service. Same three-mechanism design as the phase-5 consume (unique constraint + optimistic concurrency + Idempotency-Key). READ THE INVARIANT SECTION of phase-6-pharmacy.md, ../23-state-machines.md §3, ../22-data-dictionary.md §8.3 before coding.

Endpoint:
- POST /prescriptions/{rxId}/lines/{lineId}/dispense  — body {quantity, batch_no, expiry_date}.
  Header: Idempotency-Key (REQUIRED; 400 if missing).

Design (all three required):
1. Append-only dispense_event insert (../22 §8.3): dispense_id, prescription_line_id, dispensing_pharmacy_id, quantity, idempotency_key (UNIQUE), batch_no, expiry_date (add this column), dispensed_at, dispensed_by. Immutable — never update/soft-delete.
2. UNIQUE(idempotency_key) + CHECK/partial index ensuring cumulative quantity_dispensed ≤ quantity_prescribed. Database is the final guarantee.
3. Optimistic concurrency: prescription_line.version guards the UPDATE (WHERE prescription_line_id=@id AND version=@version AND status Active/PartiallyDispensed). 0 rows ⇒ do not double-apply; retry or return winner.

Rules & state (one DB transaction):
- Guard: quantity ≤ remaining (quantity_prescribed − quantity_dispensed); reject over-dispense.
- REJECT dispensing of any drug from an Expired/Cancelled/Rejected/fully-Dispensed prescription (../23 §3 reject rule). Also reject dispensing a lot whose expiry_date is in the past (no expired stock).
- Increment quantity_dispensed, bump version, recompute line status (PartiallyDispensed if 0<dispensed<prescribed, Dispensed if equal) and prescription status (PartiallyDispensed if any line remains available, Dispensed if all lines Dispensed). Remainder stays AVAILABLE for a later visit. Emit RxLinesDispensed and (if complete) RxDispensed via OUTBOX in the same transaction.
- Idempotent replay: same Idempotency-Key → return the ORIGINAL dispense_event, no new row, no state change (detect via UNIQUE violation → return prior outcome).
- No-reuse: an already fully-Dispensed line cannot be dispensed again → 409 with problem+json.
- Every dispense writes an immutable hash-chained audit_event (rxId, lineId, idempotency_key, quantity, batch_no, expiry_date, actor).

Acceptance criteria (US-051):
- Given remaining quantity, When I dispense ≤ remaining with batch + expiry, Then a dispense event is recorded and remaining decremented.
- Given I dispense less than prescribed, Then the Rx becomes PartiallyDispensed and the remainder stays available for later.
- Given full dispensing, Then the Rx becomes Dispensed.
- Given an expired lot or an expired/completed Rx, When I dispense, Then it is rejected.

REQUIRED tests:
- CONCURRENCY test: N parallel dispense requests against the SAME line (distinct keys) from multiple threads/connections; assert cumulative quantity_dispensed never exceeds prescribed, no over-dispense, correct final status. Real parallel DB transactions, not mocked.
- IDEMPOTENCY test: two requests with the SAME Idempotency-Key → ONE dispense_event, identical responses.
- Partial test: partial dispense → PartiallyDispensed, remainder available; remainder dispense → Dispensed.
- Reject test: expired lot and expired/completed Rx are rejected with no state change.
```

### 6.3 — Substitution with approved alternatives + out-of-stock workflow

```text
Add SUBSTITUTION and OUT-OF-STOCK handling to pharmacy-service dispensing.

READ FIRST: ../23-state-machines.md §3 "Pharmacy-specific guards", ../32-user-stories.md US-052, masterdata-service (approved alternatives).

Substitution:
- When dispensing, a pharmacist may substitute a drug ONLY with a policy-approved alternative from masterdata/policy-service (approved-alternatives list for the prescribed drug). If no approved alternative exists, do NOT substitute — route to approvals instead (do not dispense off-list).
- Record the substitution on the dispense_event (substituted_drug_id + reason) and audit it. The original prescription_line is unchanged except its accumulator.

Out-of-stock:
- If a line/quantity is out of stock, the pharmacist FLAGS it: this triggers the out-of-stock workflow (backorder/partial) WITHOUT consuming the unfilled quantity — the line stays available. Emit an event and notify the prescriber and beneficiary (notification hook, delivered by notification-service in phase 8).
- Partial dispense of the in-stock portion still works via 6.2; the unfilled remainder stays available.

Future integration (stubs only): reference a future PBM / formulary check as a clearly-marked stub/interface (IFormularyService / IPbmService) that today returns the masterdata approved-alternatives result; do not build the external integration now.

Acceptance criteria (US-052):
- Given an approved alternative, When I substitute, Then the substitution and reason are recorded and audited.
- Given no approved alternative, When I try to substitute, Then it is blocked / routed to approvals.
- Given out-of-stock, When I flag it, Then the out-of-stock workflow is triggered and the prescriber/beneficiary can be notified, and the unfilled quantity stays available.

Tests: unit (approved-alternative gate, out-of-stock does not consume the line), integration (substitution recorded on dispense_event + audited; OOS event + notification hook emitted), stub test asserting the PBM/formulary interface is called and swappable.
```

---

## Guardrails

- **No dispensing of expired drugs or completed/expired/cancelled prescriptions** — reject with a clear reason and audit; enforce both the Rx reject rule (../23 §3) and the lot expiry_date check.
- **Batch and expiry captured** on every dispense_event; `dispense_event` is append-only and immutable (history via `audit_event`).
- **Dispense is atomic, idempotent, duplicate-proof** (6.2) — unique constraint + optimistic concurrency (version) + required Idempotency-Key; covered by a real concurrency test and an idempotency test. Partial dispensing leaves the remainder available; no over-dispense.
- **No-reuse** — a fully-dispensed line cannot be dispensed again.
- **Substitution only from the policy-approved alternatives list**; otherwise route to approvals — never dispense off-list. PBM/formulary is a stubbed interface.
- **Min-necessary** — pharmacies never see investigation results/lab/imaging data (proven by an authorization test); dispensing views expose only dispensing-relevant fields.
- **Canonical states only** (../23 §3). Immutable hash-chained audit on every dispense, substitution, OOS flag, and PHI read. Outbox for events; consumers idempotent.

## Done when

- A pharmacist can search by Rx/Patient/Policy/Passport/Member and see only dispensable prescriptions with remaining quantities; an authorization test proves pharmacies cannot see investigation results.
- **Partial dispense works with batch/expiry** and the remainder stays available (Rx PartiallyDispensed); full dispense reaches Dispensed; concurrency + idempotency tests prove no over-dispense and safe replay.
- **Expired lots and expired/completed prescriptions are rejected** with no state change.
- Substitution is limited to policy-approved alternatives (recorded + audited) and the out-of-stock workflow flags/notifies without consuming the unfilled line; PBM/formulary referenced as a swappable stub.
- Concurrency + idempotency + authorization tests green; OpenAPI + README updated; audit events present. Global Definition of Done (root `CLAUDE.md`) met.
