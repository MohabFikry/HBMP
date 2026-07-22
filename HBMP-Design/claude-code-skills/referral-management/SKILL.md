---
name: Referral Management
description: Designs Mersal HBMP referral lifecycle and cross-provider hand-off — Requested → Accepted → Scheduled → Completed (+ Cancelled/Expired) — closing the loop back to the referring clinician under minimum-necessary sharing. Use when building or reviewing referrals, care hand-offs, or the digital referral network.
---

# Referral Management

## Purpose
Coordinate care across the provider network: let a treating clinician refer a beneficiary to another
provider/specialty, track the referral through acceptance, scheduling, and completion, and **close
the loop** back to the referring clinician — all while sharing only the minimum-necessary clinical
context (FR-CLIN-011, ../../23 §4).

## When to use / when not to use
- **Use when:** building/reviewing referral creation, the referral state machine, provider
  acceptance, referral-driven appointment booking, loop-closure back to the referrer, cross-provider
  hand-off, and referral-network readiness.
- **Do not use for:** the clinical encounter content itself (clinical skill); appointment slot
  mechanics (appointment skill — a referral only *pre-populates* the booking); authorization
  adjudication (approvals). Note a referral may itself be a gated service needing authorization.

## Mersal domain knowledge & rules
- **Referral entity** (`referral`, `REF-YYYY-NNNNNN`): `beneficiary_id`, `from_provider_id`,
  `to_provider_id`, `specialty`, `status`, `requested_at`; plus an append-only `referral_event`
  stream (event_type + payload) recording every step of the hand-off.
- **Lifecycle** (canonical, ../../23 §4): `Requested → Accepted → Scheduled → Completed`; plus
  `Cancelled` and `Expired`. Transitions:
  - Doctor **raises** referral (clinical need, target specialty set) → `Requested`.
  - Receiving/network provider **accepts** (availability) → `Accepted`. No acceptance in the window →
    `Expired` (system timer).
  - Appointment Team **schedules** (slot booked, links the appointment) → `Scheduled`. Not scheduled
    in window → `Expired`.
  - Consultation performed → `Completed` (encounter linked). No-show threshold → `Cancelled`
    (X3 no-show handling). Any active state can be `Cancelled` with a recorded reason.
- **Referral-driven appointment:** an `Accepted` referral pre-populates the target provider/service
  when the Appointment Team books (FR-APT-010) — this is the bridge into the appointment skill.
- **Close the loop:** completion (and the resulting encounter/results) must flow back to the
  **referring clinician** so the originating provider sees the outcome — continuity of care is the
  whole point of the referral. Notify the referrer on completion.
- **Minimum-necessary sharing (HARD RULE):** the referral carries only the reason + clinical summary
  the receiving provider needs — not the full EMR. The receiving provider gains treating-relationship
  access to that beneficiary **via the referral**, scoped to the care episode; it does not open the
  whole record. Refugee/SPI status is shared only where there is cause.
- **Digital referral network readiness:** referrals are the cross-provider coordination primitive.
  Model `from`/`to` provider explicitly and route via provider capability/catalog so the network can
  scale beyond point-to-point hand-offs.

## Key entities, states & invariants
- `referral.status` enum: `Requested, Accepted, Scheduled, Completed, Rejected, Cancelled, Expired`.
- Every transition writes an append-only `referral_event` **and** an `audit_event` (actor, from/to,
  reason where required); illegal transitions are rejected/audited as `TransitionDenied`.
- `Expired` and `Cancelled` are terminal; `Completed` links the encounter that fulfilled the referral.
- Treating access for the receiving provider is **granted by the active referral and revoked when the
  episode closes** — the referral is the ABAC relationship, mirroring the treating-relationship rule.

## How to apply
1. Referrer creates `REF-…` with target specialty + minimum-necessary summary (from the encounter).
2. If the referral/service is gated, route to authorization before it can be accepted/scheduled.
3. Receiving provider accepts → `Accepted`; Appointment Team books the referral-driven slot →
   `Scheduled` (links appointment).
4. Visit performed → `Completed`; link the new encounter and **notify/report back to the referrer**.
5. Handle no-show (→ Cancelled), timeouts (→ Expired), and explicit cancellation with reason.
6. Audit every event; share only what the receiving provider needs.

## Canonical references
- ../../23-state-machines.md (§4 referral lifecycle; §6 appointment for scheduling/no-show)
- ../../05-business-process-maps.md (referral-driven appointments P3; X3 no-show)
- ../../24-sequence-diagrams.md (cross-provider hand-off sequences)

## Guardrails
- Share minimum-necessary clinical context only; never expose the full EMR to the receiving provider.
- Grant treating access via the active referral and revoke on episode close; audit PHI reads.
- Enforce the state machine — reject illegal transitions; every step writes an append-only event.
- Always close the loop: completion and results must return to the referring clinician.
- Route gated referrals through authorization before acceptance/scheduling.
