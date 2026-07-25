# Phase 15 — Call Centre Portal (end-to-end)

**Goal:** Deliver a complete **Call Centre agent portal** for appointment management: the agent takes a call, **verifies the caller's identity**, searches the member, sees a minimum-necessary 360 (eligibility + coverage, contacts, appointments across **all branches**, open referrals and follow-ups due), then **books, reschedules, or cancels** appointments into any branch, updates contact details, and **logs the call** (reason, outcome, notes). Backend + frontend + tests, wired to the services already built.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> **Build state when this was written:** phases 0–10 are complete (core services, approvals, notification, reporting, admin, case, finance, plus the phase-9 frontend design system and role portals). Phase 14 (branch scoping & clinical sensitivity) is **designed but not yet built**. This phase therefore *adds* a portal on existing infrastructure — it does not rebuild anything.

---

## Skills to activate
> Activate `appointment-queue-management`, `ngo-healthcare-operations`, `healthcare-uiux-designer`, `policy-eligibility-engine` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management` (caller verification and phone disclosure are squarely refugee-privacy concerns). Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

**Design docs**
- [`../37-branch-scoping-and-clinical-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md) §3 — scope modes. **The Call Centre is a central hotline: MemberScoped / all branches.**
- [`../10-role-matrix.md`](../10-role-matrix.md) — the existing **Call Center** role; [`../11-permission-matrix.md`](../11-permission-matrix.md) — min-necessary field rules (call centre gets **no clinical data**).
- [`../23-state-machines.md`](../23-state-machines.md) §6 — appointment lifecycle (Booked/CheckedIn/Completed/NoShow/Cancelled) and the waitlist sub-states. Do not invent new states.
- [`../13-ux-flows.md`](../13-ux-flows.md) §3 (book/reschedule/no-show) · [`../12-ui-wireframes.md`](../12-ui-wireframes.md) · [`../14-navigation-structure.md`](../14-navigation-structure.md) · [`../0B-DESIGN-SYSTEM-UI.md`](../0B-DESIGN-SYSTEM-UI.md) (incl. §10b visual refinement v1.1) · [`../21-accessibility-checklist.md`](../21-accessibility-checklist.md).
- [`../19-audit-strategy.md`](../19-audit-strategy.md) — every PHI read and mutation is audited; [`../20-compliance-checklist.md`](../20-compliance-checklist.md) — disclosure to a caller is a data-protection event.

**Existing code you are building on (read before writing)**
- `services/emr/Api/Appointments.cs` — **already implements** `POST /appointment-slots`, `GET /appointment-slots`, `POST /appointments`, `GET /appointments`, `GET /appointments/{id}`, `POST /appointments/{id}/reschedule`, `POST /appointments/{id}/cancel`, `POST /appointments/{id}/no-show` behind scopes `appointment:read` / `appointment:write`. **Reuse these — do not fork them.** Note the phase-3 no-double-book guarantee (slot `FOR UPDATE` + `ux_appointment_active_slot` partial-unique index) and the `Idempotency-Key` + `If-Match` (xmin ETag) conventions.
- `services/eligibility/` — `GET /api/v1/reception/search` (min-necessary result card) and `GET /api/v1/eligibility/members/{id}/status`, `POST /api/v1/eligibility/check`.
- `services/patient/` — beneficiary, identifiers, contacts.
- `services/pharmacy/Api/Referrals.cs` — referral records (`REF-*`, status Requested/Accepted/Scheduled/Completed).
- `services/notification/` — templated bilingual notifications.
- `libs/authz` — `IAuthorizationEngine`, `RowScope`, `FieldProjector`, policy bundles; `libs/auth`, `libs/audit-client`, `libs/events` (outbox).
- `apps/web/src/portals/catalog.ts` — the `PORTALS` array + `Section`/`PortalDef`/`Localized` types and the `G` nav-group map. `apps/design-system` — tokens + components.
- `docs/HANDOFF.md` + `docs/BUILD-STATUS.md` — machine gotchas (`./dotnet.sh`, PostgreSQL on **:55432**, .NET 8 only, analyzers-as-errors, central package management, pnpm workspace for frontend).

---

## THE INVARIANTS

1. **Verify before you disclose.** No member detail is shown until the agent records a successful **caller verification**. An unverified session may search by identifier but sees only a "match found / no match" result plus the fields needed to complete verification — never appointments, contacts, coverage, or history.
2. **No clinical data. Ever.** The Call Centre portal must not expose diagnoses, results, prescriptions, EMR notes, or examination detail — only that an appointment exists, with its type, time, branch, doctor name and specialty. This is enforced **server-side** by projection and proven by an authorization test.
3. **All branches, MemberScoped.** The agent searches and views across all six branches and books into any branch. When phase 14 lands, this role sets `BranchUnrestricted` — it is never branch-filtered.
4. **Reuse the built appointment engine.** Booking/rescheduling/cancelling goes through the existing emr-service endpoints and preserves the no-double-book invariant, `Idempotency-Key`, and `If-Match` concurrency.
5. **Every disclosure is audited.** Verification attempts (success *and* failure), member 360 reads, appointment changes, and contact edits all write immutable hash-chained audit events, correlated to the call.
6. **Bilingual + accessible.** AR/EN with full RTL mirroring; WCAG 2.2 AA per the accessibility DoD.

---

## Prompts

### 15.1 — `callcentre-service` foundation: caller verification + call log

```text
Create callcentre-service (.NET 8, schema `callcentre`) — the contact-centre bounded context. Read
../19, ../20, ../37 §3, and libs/audit-client first. Follow the service template in CLAUDE.md
(Api/ Domain/ Infrastructure/ Tests + Dockerfile + README + compose entry) and copy the wiring shape
from an existing service such as services/case.

WHY A SEPARATE SERVICE: caller-verification records and call logs are contact-centre operational data
with their own retention and their own access boundary. Keeping them out of case-service preserves
minimum-necessary (a call agent is not a case manager, and vice versa). If the team prefers to fold
this into case-service instead, that is defensible — state the choice in an ADR, don't silently drift.

SCHEMA (migration 0001_callcentre.sql)
- call_interaction: interaction_id uuid PK (v7), call_ref varchar(20) UNIQUE ('CALL-YYYY-NNNNNN'),
  beneficiary_id uuid NULL (null until identified), agent_user_id uuid, direction CHECK IN
  ('Inbound','Outbound'), started_at timestamptz, ended_at timestamptz NULL,
  reason_code varchar(32) CHECK IN ('BookAppointment','RescheduleAppointment','CancelAppointment',
  'AppointmentEnquiry','EligibilityEnquiry','UpdateContact','Complaint','Other'),
  outcome CHECK IN ('Resolved','FollowUpRequired','Transferred','Abandoned','NoAction') NULL,
  notes text NULL, status CHECK IN ('Open','Closed'), + standard audit columns.
- caller_verification: verification_id PK, interaction_id FK, beneficiary_id,
  verified_identifiers jsonb (WHICH identifier TYPES were confirmed — e.g. ["MemberNo","DateOfBirth"]
  — NEVER the identifier VALUES), result CHECK IN ('Passed','Failed'), failure_reason varchar(64) NULL,
  verified_at, verified_by. Index (interaction_id), (beneficiary_id, verified_at DESC).

CRITICAL PRIVACY RULE: store only WHICH identifier types were confirmed, never the values the caller
recited. Values live in patient-service and must not be duplicated into the call log.

API (scopes callcentre:interaction, callcentre:verify)
- POST /api/v1/call-interactions {direction, reasonCode?} -> opens an interaction, returns call_ref.
- POST /api/v1/call-interactions/{id}/verification {beneficiaryId, verifiedIdentifierTypes[], result,
  failureReason?} -> records the attempt. Require at least TWO confirmed identifier types for a Pass
  (configurable, default 2). A Fail is recorded AND audited (do not silently discard).
- PATCH /api/v1/call-interactions/{id} {reasonCode, outcome, notes} ; POST .../close.
- GET /api/v1/call-interactions?beneficiaryId=&agentUserId=&from=&to= (agent sees own; supervisor role
  sees the team) — paginated.

VERIFICATION GATE (reusable): expose a service primitive IsVerified(interactionId, beneficiaryId)
that other endpoints in 15.2–15.4 consult. A verification is valid only for THIS interaction and
THIS beneficiary, and expires when the interaction closes.

ACCEPTANCE
- Given an open interaction, When a Pass is recorded with two identifier types, Then the interaction
  is bound to that beneficiary and IsVerified returns true.
- Given only one identifier type, Then the Pass is rejected (422) with a clear reason.
- Given a Failed verification, Then it is persisted AND audited, and IsVerified stays false.
- Given a closed interaction, Then IsVerified returns false.
- Given any verification, Then NO identifier VALUES appear in the callcentre schema (assert by
  inspecting the persisted row).

TESTS: verification rules (min types, pass/fail), binding to beneficiary, expiry on close, an
assertion that only identifier TYPES are stored, audit tests on every verification attempt.
```

### 15.2 — Member search + minimum-necessary Call Centre 360

```text
Give the agent a member-centric view across ALL branches. Read ../11 (min-necessary), ../37 §3
(MemberScoped), and the EXISTING eligibility reception search + patient/pharmacy endpoints first.
Prefer AGGREGATION over duplication: callcentre-service composes existing service calls (forwarding
the caller's token); it does not copy their data.

SEARCH (pre-verification, deliberately thin)
- GET /api/v1/call-centre/search?q= — supports member no, national ID, passport, refugee ID, UNHCR no,
  and PHONE (phone is the primary call-centre entry point — make sure it is indexed/searchable).
- Pre-verification response returns ONLY: matchCount, beneficiaryId(s), display name, and the
  identifier TYPES available to challenge on. NO appointments, NO contacts, NO coverage, NO history.
- Every search is audited.

360 (post-verification only — 403 + audit if IsVerified is false)
GET /api/v1/call-centre/members/{beneficiaryId}/summary returns a composed, PROJECTED payload:
- identity: memberNo, display name, age band, member status (four-cue status semantics for the UI);
- eligibility + coverage summary: active categories and REMAINING LIMITS (reuse eligibility-service —
  do not recompute);
- contacts: phone(s), preferred channel, address (editable in 15.4);
- appointments: upcoming AND recent past across ALL BRANCHES — appointmentId, type, status,
  scheduledStart, branch name, doctor name + SPECIALTY, and the cancel/reschedule affordances;
- open referrals (REF-*) and follow-ups due, so the agent can proactively book them;
- NOTHING CLINICAL — no diagnoses, results, prescriptions, notes, or examination detail.

Implement the projection SERVER-SIDE with the existing FieldProjector. A client must not be able to
request clinical fields by any query manipulation.

ACCEPTANCE
- Given an unverified interaction, When the agent requests the 360, Then 403 + audited.
- Given a verified interaction, Then the summary returns with appointments from every branch.
- Given the Call Centre role, When the payload is inspected, Then it contains NO clinical field
  (assert by reflection over the response type AND over the serialized JSON).
- Given a phone-number search, Then the matching member is found.

TESTS: verification gate (403 before, 200 after), cross-branch appointment inclusion, phone search,
and a MIN-NECESSARY authorization test in the style of the existing QueueMinNecessaryTests /
eligibility min-necessary tests proving no clinical leakage. Audit assertions on search + 360 read.
```

### 15.3 — Appointment management from the call (book / reschedule / cancel)

```text
Wire appointment actions for the call centre. Read ../23 §6 and services/emr/Api/Appointments.cs
FIRST — you are REUSING that engine, not writing a second one. All calls carry the agent's token and
an Idempotency-Key; reschedule/cancel carry If-Match (xmin ETag) from the prior read.

SLOT DISCOVERY ACROSS BRANCHES
- GET /api/v1/call-centre/slots?branchId=&specialtyCode=&doctorId=&from=&to= — proxies/aggregates the
  existing GET /appointment-slots so the agent can offer options at ANY branch. Branch and specialty
  are SELECTORS here, never restrictions (the call centre is MemberScoped).
- Return slot start/end, branch, doctor name + specialty. If none are free, surface the next
  available slots and the waitlist option that already exists in emr-service.

ACTIONS (each requires IsVerified for the bound beneficiary; else 403 + audit)
- POST /api/v1/call-centre/appointments -> delegates to emr POST /appointments
  {beneficiaryId, slotId, appointmentType, branchId, referralRef?, originEncounterId?}.
- POST /api/v1/call-centre/appointments/{id}/reschedule -> delegates to emr reschedule (atomic
  release-old + acquire-new in ONE transaction, as already implemented).
- POST /api/v1/call-centre/appointments/{id}/cancel {reasonCode, note?} -> delegates to emr cancel.
  reasonCode is MANDATORY from the call centre: CHECK IN ('PatientRequest','PatientUnwell',
  'TransportIssue','Rescheduling','ClinicClosure','DuplicateBooking','Other').
- Every action links the resulting appointment change to the call_interaction (store
  interaction_id/call_ref on the call-centre side; do NOT add a call column to emr's appointment table).
- On success, trigger the existing notification-service confirmation to the member's preferred channel
  (in-app/email now; SMS/WhatsApp remain the phase-8 stubs).

PRESERVE THE PHASE-3 INVARIANT: no double-booking. Do not bypass the slot lock or the
ux_appointment_active_slot partial-unique index. Re-run AppointmentBookingConcurrencyTests.

ACCEPTANCE (Given/When/Then)
- Given a verified caller and a free slot at Aswan, When the agent books, Then an appointment is
  created at that branch, linked to the call, confirmed by notification, and audited.
- Given two agents booking the SAME slot concurrently, Then exactly one succeeds and the other gets a
  409 with a clear message (invariant intact).
- Given a replayed Idempotency-Key, Then no second appointment is created.
- Given a stale If-Match on reschedule, Then 412.
- Given a cancel without a reasonCode, Then 422.
- Given an unverified interaction, Then every action returns 403 + audit.

TESTS: delegation happy paths, concurrency (reuse/extend the existing suite), idempotent replay,
If-Match 412, mandatory cancel reason, verification gate, audit on every mutation, and an
appointment↔interaction linkage test.
```

### 15.4 — Contact updates + referrals/follow-ups due

```text
Two more common call tasks. Read patient-service (contacts) and services/pharmacy/Api/Referrals.cs first.

CONTACT UPDATE (post-verification only)
- PATCH /api/v1/call-centre/members/{beneficiaryId}/contacts/{contactId}
  {value, preferredChannel?} -> delegates to patient-service. Validate phone/email format server-side.
- POST .../contacts to add a new contact; support marking one primary (respect the existing
  one-primary rule in patient-service — do not duplicate that logic here).
- Every change writes an audit event with BEFORE/AFTER (minimized) and the call_ref, and is linked to
  the interaction. Corrections are updates with history, never silent overwrites.

REFERRALS + FOLLOW-UPS DUE
- Surface open referrals (REF-*, status Requested/Accepted) and follow-ups due (from the appointment
  FollowUp linkage) in the 360, each with a "Book this" affordance that pre-fills the booking form
  (referralRef / originEncounterId) so the agent can convert them in one step.
- Booking from a referral must set appointmentType=Referral and link referralRef, so the existing
  ReferralScheduled event fires exactly as it does today.

ACCEPTANCE
- Given a verified caller, When the agent corrects the phone number, Then patient-service is updated,
  history is preserved, and the change is audited with the call_ref.
- Given an invalid phone format, Then 422 before anything is persisted.
- Given an open referral, When the agent books from it, Then the appointment is type=Referral with
  referralRef set and ReferralScheduled is emitted.
- Given an unverified interaction, Then contact update returns 403.

TESTS: validation, delegation + history, audit with call linkage, referral→booking conversion,
verification gate.
```

### 15.5 — Call Centre portal (frontend)

```text
Build the agent portal in apps/web. Read ../14-navigation-structure.md, ../12-ui-wireframes.md,
../13-ux-flows.md, ../0B-DESIGN-SYSTEM-UI.md (including §10b visual refinement v1.1),
../21-accessibility-checklist.md, and the EXISTING apps/web/src/portals/catalog.ts first — follow its
PortalDef/Section/Localized shape and permission-gated routing exactly.

REGISTER THE PORTAL
- Add a `call-centre` PortalDef to PORTALS: base "call-centre", bilingual title/eyebrow
  (EN "Call Centre" / AR "مركز الاتصال"), with sections: Active call, Member search, Appointments,
  Call history. Add a nav group if needed (e.g. G.contact = { en: "Contact centre", ar: "مركز الاتصال" }).
  Add the required Permission entries to ../authz/permissions and gate every section.

THE CALL WORKSPACE (single screen, call-shaped — this is the heart of the portal)
1. START CALL — a persistent call bar: start/close interaction, elapsed timer, reason-code select.
2. SEARCH — phone-first search box (also member no / national ID / passport / refugee ID / UNHCR).
   Pre-verification results show ONLY name + which identifier types to challenge on. Make this
   visually unmistakable: a "Not yet verified" state.
3. VERIFY — a checklist of identifier types the agent confirms verbally; requires >= 2; explicit
   Pass/Fail buttons. Failing shows guidance and keeps details hidden. The UI must NEVER display the
   stored identifier value for the agent to read out — the caller states it, the agent confirms.
4. MEMBER 360 — unlocks only after Pass: status + coverage/remaining limits, contacts (inline edit),
   appointments across all branches (upcoming + recent), open referrals / follow-ups due.
5. ACT — book (branch + specialty + doctor + slot picker with next-available and waitlist),
   reschedule, cancel (mandatory reason). Optimistic UI is NOT allowed for booking — wait for the
   server so the no-double-book result is authoritative; show a clear 409 "slot just taken" recovery.
6. WRAP UP — outcome + notes, then Close call.

DESIGN SYSTEM COMPLIANCE
- Reuse @mersal/design-system components; tokens only. Status chips use the four-cue system
  (hue + icon + shape + text). Brand teal/gold are decorative only; actions use the accessible tokens.
- Apply the v1.1 refinements (page wash, elevation + hover lift, motion base, KPI/micro-label
  treatment) so it matches the rest of the product.
- Locked/unverified state uses a neutral hue + lock icon + ghost pill + text — never colour alone.

ACCESSIBILITY (hard gate)
- Keyboard-operable end-to-end (an agent works fast, on the phone, often without a mouse): `/` focuses
  search, arrow keys move the slot list, Enter books, Esc closes dialogs; visible 3px focus ring;
  >=44px targets; focus trapped + returned in modals.
- aria-live announces verification result, booking success/failure, and cancellation.
- Full AR/RTL mirroring; both locales authored inline (no machine translation).
- axe in CI: zero serious/critical.

ACCEPTANCE
- Given an unverified caller, Then no coverage/appointments/contacts render anywhere in the DOM
  (assert the payload lacks them — not merely CSS-hidden).
- Given verification passes, Then the 360 unlocks and is announced.
- Given a booking, When the slot was just taken, Then a clear recoverable 409 state is shown.
- Given AR locale, Then the whole workspace mirrors correctly.
- Given axe + keyboard + screen-reader checks, Then all pass.

TESTS: component + integration tests for the verify gate, booking flow (incl. 409 recovery), cancel
reason validation, contact inline edit; axe in CI; a DOM assertion that pre-verification renders no
member detail.
```

### 15.6 — Call centre KPIs, notifications & end-to-end proof

```text
Close the loop. Read ../08-non-functional-requirements.md and the EXISTING reporting-service +
notification-service first.

KPIs (reporting-service read model)
- Calls handled per agent/day, average handle time, first-contact resolution (outcome=Resolved),
  reason-code mix, appointments booked/rescheduled/cancelled via the call centre, verification failure
  rate, abandoned rate. Aggregate only — NO PHI in the read model, and no clinical fields anywhere.
- Expose them for the existing dashboard contracts, each chart with an accessible data-table alternative.

NOTIFICATIONS
- Confirmations on book/reschedule/cancel to the member's preferred channel, bilingual templates
  (AR/EN), reusing notification-service. Templates must contain NO clinical detail — appointment time,
  branch, doctor name/specialty only. Run the existing template linter if present.

END-TO-END TEST (the proof this phase works)
Write an E2E test that runs the whole journey:
  open interaction -> search by PHONE -> verify with two identifier types -> load 360 (assert no
  clinical fields) -> book an appointment at a chosen branch -> assert notification queued and
  appointment visible in emr -> reschedule it -> cancel it with a reason -> close the call with an
  outcome -> assert the audit chain contains: search, verification, 360 read, book, reschedule,
  cancel, contact/interaction updates, each correlated by call_ref.

ACCEPTANCE
- Given the E2E test, Then every step passes and the audit chain is complete and hash-linked.
- Given the KPI read model, Then it contains no PHI and no clinical field.
- Given a booking notification, Then it is bilingual and clinical-free.

TESTS: the E2E above, KPI projection tests, notification template lint/content test.
Update docs/BUILD-STATUS.md, the service README, and add an ADR for the callcentre-service boundary
decision. Full suite green: ./dotnet.sh test HbmpPlatform.sln -c Release (+ pnpm frontend tests).
```

---

## Guardrails

- **Verify before disclose** is the defining control of this portal — enforce server-side, never only in the UI.
- **Never store identifier values** in the call-centre schema; only which types were confirmed.
- **No clinical data** reaches this role: no diagnoses, results, prescriptions, notes, or examination detail — proven by an authorization test over the serialized payload.
- **Reuse the built appointment engine**; the phase-3 no-double-book guarantee, `Idempotency-Key`, and `If-Match` must survive untouched.
- **MemberScoped / all branches** — this role is never branch-filtered; branch and specialty are selectors, not restrictions. When phase 14 lands, set `BranchUnrestricted` for it.
- **Audit everything**: searches, verification passes *and* failures, 360 reads, every appointment/contact mutation — correlated by `call_ref`.
- Accessibility DoD is a merge gate; both locales authored inline.
- Respect the machine gotchas in `docs/HANDOFF.md` (`./dotnet.sh`, PG :55432, .NET 8 APIs only, analyzers-as-errors, central package management, pnpm workspace).

## Done when

- [ ] `callcentre-service` exists with `call_interaction` + `caller_verification`, and stores **no identifier values**.
- [ ] Verification requires ≥2 confirmed identifier types; failures are persisted and audited; verification is per-interaction and expires on close.
- [ ] Pre-verification search reveals only name + challengeable identifier types; the 360 is **403 until verified**.
- [ ] The 360 shows eligibility/coverage + remaining limits, contacts, **appointments across all six branches**, open referrals and follow-ups due — and **no clinical field**, proven by an authorization test.
- [ ] Agents can **book, reschedule and cancel** through the existing emr endpoints; cancel requires a reason code; concurrency, idempotency and `If-Match` behaviour are intact.
- [ ] Contact details can be corrected mid-call with history + audit; referrals/follow-ups convert to bookings in one step.
- [ ] The Call Centre portal is registered in `apps/web/src/portals/catalog.ts` with permission-gated sections and the call-shaped workspace.
- [ ] Bilingual AR/EN with full RTL; axe zero serious/critical; keyboard-only operation end-to-end.
- [ ] Notifications are bilingual and clinical-free; KPIs contain no PHI.
- [ ] The end-to-end journey test passes with a complete, correlated audit chain.
- [ ] `docs/BUILD-STATUS.md` ticked, README + ADR written, full backend and frontend suites green.
