# 8. callcentre-service is its own bounded context (not folded into case-service)

Date: 2026-07-26
Status: Accepted
Phase: 15 (Call Centre Portal)

## Context

Phase 15 adds a **contact-centre agent portal**: an agent takes a call, verifies the caller, views a
minimum-necessary member 360 across all branches, and books/reschedules/cancels appointments, updates contacts and
logs the call. The call log and caller-verification records are new persistent data. Two homes were considered:

1. Fold it into **case-service** (already coordinates a beneficiary across services).
2. A **new callcentre-service** bounded context.

## Decision

Create a **separate `callcentre-service`** (schema `callcentre`) with two aggregates — `call_interaction` and
`caller_verification` — and the reusable `VerificationService` gate.

## Rationale

- **Minimum-necessary access boundary.** A call agent (`call_center`, tier T2) is *not* a case manager (tier T3, sees
  a coordination clinical summary incl. diagnosis). Folding the two together would blur two different need-to-know
  envelopes. Keeping them apart lets each service authorize with its own overlay and lets the call agent's data have
  its own retention and its own audit surface.
- **Different lifecycle & retention.** Contact-centre interaction/verification records are operational telephony data
  with their own retention; case files are long-lived coordination records. Separate schemas keep those clocks apart.
- **Composition over duplication.** callcentre-service *aggregates* existing services (eligibility reception search,
  emr appointment engine, patient contacts, pharmacy referrals) by forwarding the caller's token — it does not copy
  their data. So a separate service adds no data-ownership duplication; it only owns the call log + verification.
- **The appointment engine is reused, not forked.** Booking/rescheduling/cancelling delegates to the existing
  emr-service endpoints, preserving the phase-3 no-double-book invariant, `Idempotency-Key`, and `If-Match`.

## Consequences

- One more deployable service, Kong route, and migration. Acceptable — it mirrors every other bounded context.
- The "verify before you disclose" control lives in one place (`VerificationService`) and is unit- and
  integration-tested there, rather than being an implicit branch inside case-service.
- If the team later prefers to co-locate telephony with coordination, this is reversible: the two aggregates and the
  gate move as a unit. This ADR records the boundary so that move is deliberate, not accidental drift.
