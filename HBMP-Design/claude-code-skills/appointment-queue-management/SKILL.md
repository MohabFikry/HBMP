---
name: Appointment & Queue Management
description: Designs Mersal HBMP scheduling and front-desk flow — appointment types, provider availability, atomic slot booking (no double-book), reschedule/cancel, no-show handling with waitlist backfill, walk-in queue, and reminders. Use when building or reviewing appointments, provider schedules, reception queues, or no-show/waitlist logic.
---

# Appointment & Queue Management

## Purpose
Enable eligible beneficiaries to be scheduled or seen walk-in against provider availability without
double-booking, and give the front desk a live queue/day-list to run the waiting room — including
no-show detection, slot release, and waitlist promotion (FR-APT-001..011, X3/P3).

## When to use / when not to use
- **Use when:** building/reviewing appointment booking, provider availability templates, slot
  reservation, reschedule/cancel, no-show + backfill, the walk-in/reception queue, waitlist
  promotion, or appointment reminders/notifications.
- **Do not use for:** the clinical encounter content (see clinical skill); eligibility math (call the
  eligibility service); referral lifecycle beyond the fact that an accepted referral pre-populates a
  booking (see referral skill).

## Mersal domain knowledge & rules
- **Appointment sources / types:** scheduled (self/Call Center), **walk-in / same-day** (Reception
  queue, no pre-booked slot — refugee clinics are heavily walk-in), **referral-driven** (an accepted
  `REF-…` pre-populates target provider/service), and **follow-up** (clinical). (FR-APT-001/002/010.)
- **Eligibility at booking time:** validate eligibility when booking so ineligible/uncovered services
  are not scheduled (FR-APT-001). Call the eligibility service; do not re-implement it.
- **Provider availability** (FR-APT-006): schedules/availability templates define working hours, slot
  length, capacity, and blackout dates. **No slot can be booked without availability.**
- **No double-booking (HARD RULE, FR-APT-011):** slot reservation must be **atomic** — exactly one
  booking wins a slot under concurrency (optimistic concurrency / unique constraint on the slot).
- **Reschedule** (FR-APT-003) preserves history and notifies the beneficiary; **cancel** (FR-APT-004)
  requires a reason code and **frees the slot** for reuse.
- **No-show** (FR-APT-005, X3): after a configurable **grace period**, hold the slot briefly, then
  mark `NoShow`, **free the slot, and promote the waitlist**. **Repeated no-shows route to
  Case-Manager review (vulnerability vs. abuse) — never used punitively without case review.**
- **Waitlist / queue (P3):** when no slot is available, the request enters a waitlist; promotion is
  automatic on cancel/no-show, ordered by **priority score** (clinical urgency, referral,
  vulnerability). Waitlist entries `Expire` when their window lapses.
- **Reception queue / day-list** (FR-APT-007): live view of checked-in, waiting, in-consultation, and
  completed. Reception is `provider:own` + today's assigned beneficiaries; **T1 data only, no EMR**.
- **Encounter on check-in** (FR-APT-008): checking in generates `ENC-…`, linking eligibility
  snapshot, provider, and beneficiary — the hand-off to the clinical workflow.
- **Branch awareness (37, phase 14):** `appointment`, `appointment_slot`, `provider_availability`,
  `waitlist_entry`, and the queue ticket carry **`branch_id`** alongside the existing `location_id`
  (a Mersal-branch booking sets `branch_id`; an external provider-location booking leaves it NULL).
  **BranchScoped** roles (Reception, Appointment Coordinator, Nurse, Doctor operational lists,
  Branch/Clinic Manager) see **only the active branch** — a cross-branch request is **denied (403),
  never silently empty**. **MemberScoped** roles (approvals, Medical Director, Case/Finance/Claims,
  managers, admin) see **all branches** with an optional branch filter, never a restriction; external
  providers stay **ProviderScoped** and are untouched. Active branch comes from `X-Active-Branch`,
  defaulting to the user's Home branch and **always validated server-side** against the permitted set.
- **Doctor↔branch rule:** a doctor may only have availability created or be booked at a **branch they
  are assigned to** — otherwise `422` with a clear reason, validated at availability creation *and* at
  booking (never UI-only). Doctor pickers filter by **active branch + specialty**.
- **The no-double-book guarantee survives the retrofit:** adding `branch_id` must not weaken the
  phase-3 `FOR UPDATE` lock or the partial-unique active-slot index — re-run the concurrency suite.
- **Reminders:** event-driven, **bilingual (AR/EN)**, and **data-minimized — no diagnoses/clinical
  detail on outbound channels**. **In-app notifications are the current channel; SMS/WhatsApp are
  future.** Design templates channel-agnostic so SMS/WhatsApp can be added later.

## Key entities, states & invariants
- Appointment/Encounter lifecycle (../../23 §6): `Requested → (Waitlisted | Scheduled) →
  CheckedIn → InConsultation → Completed`; plus `Cancelled`, `NoShow` (→ rebook), `Expired`
  (waitlist). `appointment.status` enum: `Booked / CheckedIn / Completed / NoShow / Cancelled`.
- Booking is atomic; cancel/no-show emit slot-free events that **drive waitlist promotion**.
- `NoShow → Scheduled` rebook is allowed within re-booking policy; repeat no-shows escalate to Case
  Manager. Every transition (book, promote, no-show, cancel) is audited with reason where required.
- Call Center bookings: agent sees only scheduling-relevant, non-clinical data (FR-APT-009).

## How to apply
1. Determine type (scheduled/walk-in/referral/follow-up). For walk-in, create a queue entry directly.
2. Check eligibility; search slots by specialty + priority against availability templates.
3. Slot available → atomic reserve → `Scheduled`; none → `Waitlisted` with priority score.
4. On cancel/no-show → free slot → promote next waitlist entry by priority; notify.
5. On arrival → check-in → generate encounter → hand to clinical workflow.
6. Fire bilingual, minimized reminders (in-app now; keep templates channel-ready for SMS/WhatsApp).

## Canonical references
- ../../05-business-process-maps.md (P3 appointments; X3 no-show & cancellation)
- ../../13-ux-flows.md (booking / queue flows)
- ../../23-state-machines.md (§6 appointment/encounter lifecycle)
- ../../37-branch-scoping-and-clinical-sensitivity.md (§2–3 branch model, scope modes; §4 doctor↔branch)

## Guardrails
- Slot reservation must be atomic — never allow two bookings to win the same slot.
- Branch scoping is **server-side and narrowing only** — it never replaces eligibility, treating-
  relationship, or min-necessary rules, and a cross-branch read returns a denial, not an empty list.
- Never trust `X-Active-Branch`; never let a client widen its own permitted branch set.
- Never book against unavailable capacity or a blackout; never skip the booking-time eligibility check.
- Free the slot and promote the waitlist on every cancel/no-show — no wasted capacity.
- Reception queue exposes T1 identity/appointment data only — never EMR.
- Reminders carry no clinical detail; honour AR/EN preference; treat SMS/WhatsApp as future channels.
