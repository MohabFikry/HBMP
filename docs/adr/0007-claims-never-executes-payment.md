# ADR 0007 — The claims platform NEVER executes payment

## Status
Accepted (Phase 10b.8).

## Context
claims-service produces **settlement advice** — the reviewed, decided, and totalled record of what is payable to a
provider (or a reimbursement cohort) for a period. A settlement system could plausibly also *pay*: call a bank rail,
push a payout, integrate a payment gateway. Mersal deliberately does not.

## Decision
**The platform never moves money.** There is no payment execution, no bank/payment-rail integration, and no payout
endpoint in claims-service or any other Mersal service.

- On a **Decided** batch, claims-service generates one **immutable** settlement advice per payee (append-only
  `settlement_advice` row + content hash + a WORM document reference in document-service), freezes the batch rollups,
  and moves the batch to `SettlementIssued`. Regeneration writes a **new version** referencing the superseded one — it
  never overwrites.
- **Exports** (CSV/XLSX/PDF) are the hand-off projection for Finance/provider. They carry **zero clinical fields**, are
  **audited**, and are **provider-isolated**.
- Finance/treasury executes payment **externally**, then may record the external payment **reference** back against the
  batch via `POST /claim-batches/{id}/payment-reference` (scope `claims:settle`, SoD-separated from `claims:decide`).
  This **records a fact and moves the batch to `Closed`; it initiates nothing.**

## Enforcement
- No payout/transfer code path exists — asserted by `NoPaymentPathTests`, which scans the claims production source for
  payment-execution identifiers (`ExecutePayment`, `TransferFunds`, `BankTransfer`, …) and fails the build if any appear.
- The settlement advice and the payment reference are **append-only** (trigger + no `UPDATE`/`DELETE` grant); the WORM
  document is object-locked in document-service.
- `claims:settle` is a distinct scope held by Finance/manager roles, never by the `claims:decide` decider (SoD).

## Consequences
- Mersal integrates with whatever treasury/banking process it already uses; the platform is not a money transmitter and
  carries none of that regulatory surface.
- The settlement advice + content hash are the auditable source of truth for what was authorised to pay; the actual
  payment is reconciled back by reference only.
