# ADR 0006 — No double-billing is a database guarantee; the platform never executes payment

- Status: Accepted
- Date: 2026-07-26
- Context: Phase 10b (claims-service)

## Context

Phase 10b introduces `claims-service`, which turns delivered/authorized services into decided, settled financial
records. Two properties are safety-critical and must not depend on application code being correct:

1. **A delivered service must never be paid twice.** Auto-derived claims (from `OrderLinesConsumed` /
   `RxLinesDispensed`), provider-submitted claims, and beneficiary reimbursements can all reference the same
   underlying `order_fulfillment` / `dispense_event` row.
2. **The platform must never move money.** Mersal's treasury/finance pays externally; the system's output is a
   settlement advice, not a payment.

## Decision

1. **No double-billing is enforced by a partial unique index**, not by application logic:
   `CREATE UNIQUE INDEX ux_claim_line_fulfillment ON claims.claim_line (fulfillment_ref) WHERE fulfillment_ref IS NOT
   NULL AND status <> 'Void';`. At most one live (non-`Void`) payable line may reference a given fulfillment record.
   A losing writer fails on SQLSTATE 23505 and the service maps it to `DUPLICATE_CLAIM` (409 problem+json) — never a
   silent second payable line. This is proven by a real N-way parallel-transaction concurrency test, not a mock.
   A corrected re-claim is possible only after the original line is `Void`ed (which frees the reference).

2. **Intake is idempotent** on event id (`processed_event`), so at-least-once event delivery cannot create
   duplicate lines.

3. **Pricing is never guessed.** The tariff is read from `contract_service_line.agreed_price` for the code + service
   date. No tariff ⇒ `NO_TARIFF` + `RequiresManualReview`, `contract_price` null. A failed tariff call yields "no
   tariff" (manual review), never a defaulted price.

4. **No payment execution exists anywhere in the platform.** There is no payout endpoint, no bank-rail integration.
   The settlement advice (10b.8) is immutable and WORM-stored; Finance may *record* an external payment reference
   afterward, which initiates nothing.

5. **Claims ≠ diagnosis.** The `claims` schema carries no clinical column; every claims projection is a server-side
   allow-list DTO. Medical-necessity questions route to a clinical reviewer (in `approvals`), whose opinion never
   lands in `claims` and is never seen by the Claims Officer.

## Consequences

- The double-billing and clinical-firewall guarantees survive application bugs — the DB and the type system are the
  backstops. RLS (`app.tenant_id` / `app.provider_id`) adds provider isolation as an independent fourth layer.
- Corrections flow as append-only `claim_adjustment` rows or a compensating `Void` + re-claim; nothing is mutated or
  hard-deleted (full history via `audit_event`).
- Consumers must be prepared for `DUPLICATE_CLAIM` as a normal outcome of racing/redelivery, not an error to retry.
