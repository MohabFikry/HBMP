# Phase 5 — Lab & Imaging Fulfillment (Atomic Idempotent Consume)

**Goal:** Give lab and imaging providers a fulfillment slice on top of phase-4 orders: an authorized **order queue + search** that exposes only the lines a provider may act on (labs cannot see prescriptions), the **atomic, idempotent, duplicate-proof consume** endpoint that is the heart of the platform, and **result upload** that attaches a report to document-service and routes it to the ordering doctor/approvals. Release **R3**. Built in parallel with phase 6 (both share the same consume/no-reuse pattern).

Back to master list: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> Root `CLAUDE.md` already defines stack, conventions, security, audit, testing, and Definition of Done. This file adds phase-5 scope only.

---

## Skills to activate
> Activate `clinical-workflow-designer`, `healthcare-database-architect`, `provider-network-management` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [`../07-functional-requirements.md`](../07-functional-requirements.md) — provider fulfillment requirements.
- [`../22-data-dictionary.md`](../22-data-dictionary.md) — §7.1 `investigation_order`, §7.2 `order_line`, **§7.3 `order_fulfillment` (append-only)**. The `idempotency_key` UNIQUE constraint and immutable append-only rows are authoritative.
- [`../23-state-machines.md`](../23-state-machines.md) — §2 Investigation Order, especially **"Atomic-consume guard (invariant detail)"**: precondition, effect, idempotency, no-reuse.
- [`../24-sequence-diagrams.md`](../24-sequence-diagrams.md) — the atomic-consume sequence (client → orders-service → DB with unique constraint + version + idempotency key).
- [`../26-testing-strategy.md`](../26-testing-strategy.md) — concurrency test and authorization test expectations.
- [`../32-user-stories.md`](../32-user-stories.md) — US-040, US-041, US-042.
- Reference: [`../11-permission-matrix.md`](../11-permission-matrix.md) (min-necessary: labs ≠ prescriptions), document-service (phase 0) for blob storage.

Master data: LOINC/CPT via masterdata-service.

---

## THE INVARIANT (read before writing any code in 5.2)

Order-line consumption is **ATOMIC, IDEMPOTENT, and DUPLICATE-PROOF**. Three mechanisms combine — all three are required:

1. **Unique partial index** — at most ONE active consumption per line. Insert into the append-only `order_fulfillment` table; a `UNIQUE(idempotency_key)` constraint plus a unique partial index guaranteeing a line cannot be consumed beyond its ordered quantity. The DB, not the app, is the final arbiter.
2. **Optimistic concurrency (version)** — `order_line` carries a `version` (xmin or explicit int). Consume reads the line, computes the new `quantity_consumed`, and updates `WHERE order_line_id = @id AND version = @version`. Zero rows updated ⇒ someone else won the race ⇒ retry or return the winner's result. No lost updates.
3. **Idempotency-Key** — the mutating endpoint REQUIRES an `Idempotency-Key` header. Replaying the same key returns the PRIOR outcome (same fulfillment row) with no state change. Store the key in `order_fulfillment.idempotency_key` (UNIQUE) so a duplicate insert is rejected by the DB and mapped to "return prior result".

Semantics: partial consume → line `PartiallyUsed`, order `PartiallyUsed`, remaining lines stay **Active**. All lines consumed → order `Completed`. A **used line can never return to available** (no-reuse); only Cancelled/Expired void *unused* lines. Everything happens in a single DB transaction. `order_fulfillment` rows are immutable and append-only (no update/soft-delete). Full history via `audit_event`.

---

## Prompts

### 5.1 — Provider order queue + authorized search

```text
Extend orders-service with a PROVIDER-FACING order queue + search. .NET 8, REST /api/v1 + OpenAPI 3.1.

READ FIRST: ../22-data-dictionary.md §7, ../11-permission-matrix.md, ../32-user-stories.md US-040.

Endpoints:
- GET /investigation-orders/queue — orders/lines available for the authenticated provider's facility.
- GET /investigation-orders/search?patientIdentifier=... | orderNo=... — search by patient identifier OR order number.

Authorization (min-necessary, the core of this prompt):
- Return ONLY order lines authorized for the calling provider: matching order_type to the provider's capability (Lab vs Imaging), the provider's facility/network ownership (ABAC provider-ownership), and status Active/PartiallyUsed. 
- Filter to available lines only: a used/Completed/Cancelled/Expired line/order does NOT appear as available.
- A lab/imaging provider MUST NOT be able to see prescriptions or any pharmacy data — this service does not expose them, and cross-service reads are denied. Field-level projection returns only what the provider needs to fulfil (patient identifier, order line code/description, quantity remaining), never diagnoses/notes beyond clinical context permitted by ../11.
- Enforce at policy engine (scope orders:read) AND row/field level. PHI reads audited.

Acceptance criteria (US-040):
- Given an order for my facility, When I search by patient identifier or order number, Then I see only lines authorized for my provider and cannot see prescriptions.
- Given an order not for me or already used, When I search, Then it does not appear as available.

Tests: integration (queue/search filtering by facility + status), AUTHORIZATION test proving (a) a lab provider cannot retrieve another facility's order, and (b) a lab provider cannot read prescriptions/pharmacy data (must return 403/empty, and attempt audited). Assert used/expired lines are excluded.
```

### 5.2 — Atomic idempotent consume (the heart of the phase)

```text
Implement the ATOMIC, IDEMPOTENT, DUPLICATE-PROOF consume in orders-service. This is the single most important endpoint in the system — implement the full three-mechanism design (unique constraint + optimistic concurrency + Idempotency-Key). READ THE INVARIANT SECTION of phase-5-lab-imaging.md and ../23-state-machines.md §2 "Atomic-consume guard" and ../22-data-dictionary.md §7.3 before coding.

Endpoint:
- POST /investigation-orders/{orderId}/consume  (per-line consume; body lists {orderLineId, quantity}).
  Header: Idempotency-Key (REQUIRED; reject 400 if missing).

Design (all three required):
1. Append-only order_fulfillment insert per consumed line (../22 §7.3): fulfillment_id, order_line_id, performing_provider_id, quantity, idempotency_key (UNIQUE), consumed_at, consumed_by. Rows are IMMUTABLE — never update or soft-delete.
2. UNIQUE(idempotency_key) + a unique partial index / CHECK ensuring cumulative quantity_consumed never exceeds quantity_ordered (0 ≤ consumed ≤ ordered). The database is the final guarantee against double-consume.
3. Optimistic concurrency: order_line has a version; the consume UPDATE is guarded WHERE order_line_id=@id AND version=@version (and status Active/PartiallyUsed). If 0 rows affected, do NOT double-apply — re-read and either retry or return the prior/winning result.

Transaction & state:
- One DB transaction: insert fulfillment row(s), increment quantity_consumed, bump version, recompute line status (PartiallyUsed if 0<consumed<ordered, Completed if consumed==ordered), recompute order status (PartiallyUsed if any line remains available, Completed if all lines Completed). Emit OrderLinesConsumed and (if applicable) OrderCompleted via OUTBOX in the same transaction.
- Idempotent replay: same Idempotency-Key → return the ORIGINAL result (200 with the prior fulfillment), no new row, no state change. Detect via the UNIQUE constraint violation on idempotency_key and map to "return prior outcome".
- No-reuse: a Completed/used line can NEVER be consumed again → return 409 "already used" with problem+json, no state change.
- Partial fulfillment leaves remaining lines Active and the order PartiallyUsed.
- Every consume writes an immutable hash-chained audit_event recording (orderId, lineId, idempotency_key, quantity, actor).

Authorization: only a provider authorized for the line (5.1 rules) may consume; scope orders:consume; validated at gateway AND service.

Acceptance criteria (US-041):
- Given an Active line, When I consume it (with an Idempotency-Key), Then it is locked and marked consumed in a single atomic transaction; line/order status recomputes correctly.
- Given the line is already consumed, When I attempt to consume again (new key), Then I get "already used" (409) and NO state change.
- Given I replay the SAME Idempotency-Key, Then I get the original result and NO new fulfillment row (idempotent).
- Given I consume some of several lines, Then the order becomes PartiallyUsed and remaining lines stay Active.

REQUIRED tests (do not mark done without these):
- CONCURRENCY test: fire N parallel consume requests for the SAME line (distinct keys) from multiple threads/connections; assert EXACTLY ONE succeeds, all others get "already used"/conflict, quantity_consumed never exceeds ordered, and exactly one fulfillment row exists. Prove no double-consume under real parallel DB transactions (not mocked).
- IDEMPOTENCY test: two requests with the SAME Idempotency-Key produce ONE fulfillment row and identical responses.
- Partial test: consuming a subset yields PartiallyUsed with remaining lines Active; consuming the remainder yields Completed.
- No-reuse test: a Completed line cannot be consumed again.
```

### 5.3 — Result upload, report attachment, and routing

```text
Add result upload to orders-service, integrating document-service (Blob) and notification routing.

READ FIRST: ../22-data-dictionary.md §7.3 (result_document_id), ../24-sequence-diagrams.md (result upload/route), ../32-user-stories.md US-042.

Endpoint:
- POST /investigation-orders/{orderId}/lines/{lineId}/result — upload result value(s) + attach a report document.

Behaviour:
- The report file goes to document-service (Blob, CMK); it is malware-scanned; store the returned blob ref as order_fulfillment.result_document_id (link to the consumed line's fulfillment row). A result may only be uploaded for a CONSUMED line.
- Route the result to the ordering doctor (and to approvals if the order was approval-gated) via an outbox event (ResultReady/OrderResultUploaded); notification-service (phase 8) delivers it; the ordering doctor can read the result and attached report.
- When all lines are fulfilled AND results uploaded per the completion rule, the order is Completed (emit OrderCompleted if not already).
- Min-necessary: the result/report is visible to the ordering doctor and approval team, NOT to unrelated roles/facilities. Result PHI reads audited. Mutations hash-chained audited; append-only.

Acceptance criteria (US-042):
- Given a consumed line, When I upload a result and attach a report, Then it is stored (document-service) and routed to the ordering doctor/approvals.
- Given all lines fulfilled, Then the order becomes Completed.
- Given a line NOT yet consumed, When I try to upload a result, Then it is rejected.

Tests: integration (upload → document-service blob ref persisted on fulfillment; result routing event emitted via outbox), authorization test (only ordering doctor/approvals can read the result; another facility cannot), unit (completion recompute).
```

---

## Guardrails

- **The consume invariant (5.2) is the heart of this phase** — unique constraint + optimistic concurrency (version) + required Idempotency-Key, all in one DB transaction. It MUST be covered by a real concurrency test (parallel transactions, not mocks) proving no double-consume, and an idempotency test proving replay returns the prior result. No exceptions.
- **Append-only fulfillment** — `order_fulfillment` rows are immutable; never update or soft-delete; history via `audit_event`.
- **No-reuse** — a used/Completed line can never return to available; only Cancelled/Expired void *unused* lines.
- **Canonical states only** (../23 §2): Active → PartiallyUsed → Completed; partial fulfillment leaves the remainder Active.
- **Min-necessary** — labs/imaging see only their authorized lines and never prescriptions/pharmacy data; results visible only to ordering doctor/approvals. Proven by authorization tests.
- **Immutable hash-chained audit** on consume, result upload, and PHI reads. Outbox for all events; consumers idempotent.

## Done when

- A provider's queue/search returns only authorized available lines, and an authorization test proves a lab cannot see prescriptions or another facility's orders.
- A line consumes **exactly once under concurrency** (proven by a parallel-request test), replaying the same Idempotency-Key returns the original result with no new row, a partial consume leaves the remainder **Active** with the order **PartiallyUsed**, and a used line cannot be reused (409).
- A result is uploaded for a consumed line, its report is stored in document-service, and the result is routed to the ordering doctor/approvals; the order reaches **Completed** when all lines are fulfilled.
- Concurrency + idempotency + authorization tests green; OpenAPI + README updated; audit events present. Global Definition of Done (root `CLAUDE.md`) met.
