# 14. Phase-14 clinical-sensitivity retrofit scope

Date: 2026-07-26
Status: Accepted
Phase: 14 / 16.6

## Context

Phase 14 introduced branch scoping, practitioner specialty, and clinical sensitivity (examination-type
sensitivity + the sensitive-result release workflow). The 16.6 audit (H4) found the retrofit was not applied
uniformly: orders-service enforced sensitive-result gating, but approvals-service (the standing EMR oversight
consumer) was never retrofitted, so BUILD-STATUS's "Phase 14 fully ☑" overstated coverage. This ADR records
what the sensitivity model does and does not cover, so the boundary is explicit rather than assumed.

## Decision

The clinical-sensitivity model covers, as of 16.6:

- **Where sensitivity lives:** investigation *results* (orders-service), keyed off the examination-type's
  sensitivity level (masterdata). emr's own clinical records (SOAP notes, diagnoses, vitals) are **Standard** —
  they are not modelled with per-record sensitivity; the sensitive vector is the investigation result.
- **Enforcement points:** orders direct reads (`SensitiveResultGate`) AND the approvals review aggregation
  (`ReviewView` via `SensitiveDisclosure`) — see ADR-0012. The emr `/clinical-context` oversight aggregation
  carries the sensitivity contract (`SensitivityLevel` + `CallerHasAccess`) even though its own items are
  Standard, so a future sensitive item flowing through it is gated by construction.
- **Branch scope** and **practitioner specialty** remain as built in Phase 14 (BranchScope ABAC, worklist
  scoping, doctor↔branch assignment) — unchanged by this ADR.

Explicitly **out of scope** (deferred, tracked):

- Per-note / per-diagnosis sensitivity in emr (would require a sensitivity model on clinical records).
- The full emr `/clinical-context` surfacing of orders results into the review (the aggregation returns
  emr-owned data today; wiring orders results through it is a later integration — the gate is ready).
- Field-class projection of patient identity/contact (no pii/contact classes in the FieldAccessMatrix yet;
  see H2 note).

## Consequences

- The audit finding H4 is closed at the two disclosure points that matter; the boundary above prevents a
  future "we thought Phase 14 covered that" gap by naming what is Standard-by-design vs genuinely sensitive.
