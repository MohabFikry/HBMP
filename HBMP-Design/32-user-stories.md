# 32 — User Stories & Acceptance Criteria

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [31-product-backlog.md](31-product-backlog.md) · [07-functional-requirements.md](07-functional-requirements.md) · [13-ux-flows.md](13-ux-flows.md) · [23-state-machines.md](23-state-machines.md)

Detailed user stories with **Given/When/Then** acceptance criteria for the highest-value features. Every story inherits two global acceptance criteria (not repeated each time):

- **G-A11y:** the screen meets the accessibility gate in [21-accessibility-checklist.md](21-accessibility-checklist.md) (keyboard, screen reader, non-color status, AR/EN RTL, ≥44px targets).
- **G-Audit:** every create/update/decision/consume/dispense/export writes an immutable audit event ([19-audit-strategy.md](19-audit-strategy.md)) and honors the minimum-necessary field rules ([11-permission-matrix.md](11-permission-matrix.md)).

Priority: M/S/C. Format: **US-nnn** · role · story · AC.

---

## Phase 1 — Registration

**US-001 (M) — Register by any identifier**
*As a Registration officer, I want to register a beneficiary using National ID, Passport, Refugee ID, UNHCR number, or Member number so that anyone eligible can be enrolled regardless of documents held.*
- Given a new beneficiary, When I enter any one supported identifier, Then the system accepts it, validates format, and stores it with its type and issuing authority.
- Given an identifier that already exists, When I submit, Then the system warns and offers to open/merge the existing record rather than creating a duplicate.
- Given required personal/contact fields are missing, When I try to proceed, Then inline + summary errors block progress and name each missing field.

**US-002 (M) — Upload & validate documents**
*As a Registration officer, I want to upload supporting documents so that eligibility can be verified.*
- Given a file, When it exceeds size or is a disallowed type, Then it is rejected with a clear reason.
- Given an accepted file, When uploaded, Then it is malware-scanned and attached to the beneficiary with timestamp and uploader.

**US-003 (M) — Approve & activate registration**
*As a Beneficiary Management approver, I want to review and approve registrations so that only eligible people are activated.*
- Given a Pending registration, When I approve, Then status becomes Active, a Member number is issued, and an eligibility snapshot is created.
- Given incomplete info, When I choose Request Info, Then it returns to the officer with my notes and stays Pending.
- Given ineligibility, When I Reject, Then a reason is mandatory and recorded.

**US-004 (S) — Manage status lifecycle**
*As a Beneficiary Management officer, I want to suspend, expire, block, or reactivate a beneficiary so that coverage reflects reality.*
- Given an Active member, When I suspend/block with a reason, Then eligibility checks immediately reflect the new status.
- Given a Suspended member, When I reactivate, Then history records both transitions.

## Phase 2 — Eligibility

**US-010 (M) — Reception eligibility search**
*As a Reception officer, I want to search by ID/Passport/Card/Policy/Phone so that I can confirm eligibility fast.*
- Given a valid identifier, When I search, Then I see a result card with status, coverage, remaining limits, and a visit-history summary within 2s (p95).
- Given no match, When I search, Then I get an empty state suggesting another identifier or registration.
- Given my Reception role, When I view the card, Then no EMR/diagnosis data is shown.

**US-011 (M) — Status-driven visit gating**
*As a Reception officer, I want the system to block visit creation for ineligible members so that ineligible care isn't rendered.*
- Given Expired/Suspended/Blocked/Inactive, When I try to create a visit, Then it is blocked with guidance (e.g., refer to Case Manager).
- Given Active, When I proceed, Then an encounter is created and appears in the clinician queue.

## Phase 3 — Appointments

**US-020 (M) — Book scheduled appointment**
*As an Appointment coordinator, I want to book against doctor availability so that patients get a slot.*
- Given available slots, When I select one, Then it is reserved and confirmed (status Scheduled).
- Given no availability, When I search, Then I'm offered the next slots or a waitlist.

**US-021 (M) — Reschedule / cancel**
- Given a Scheduled appointment, When I reschedule, Then the old slot is released and the new one confirmed, both audited.
- Given a cancellation, When confirmed, Then the slot is released.

**US-022 (S) — No-show handling**
*As an Appointment coordinator, I want to mark no-shows so that slots are reused and patterns tracked.*
- Given a passed appointment not checked-in, When I mark No-show, Then it's recorded, the slot can be backfilled, and reporting captures it.

## Phase 4 — Consultation / EMR

**US-030 (M) — Treating-only clinical access**
*As a Doctor, I want to open only patients I am treating so that access stays minimum-necessary.*
- Given a patient not assigned to me, When I try to open the record, Then access is denied and the attempt is audited.
- Given an assigned patient, When I open the encounter, Then I see summary, history, diagnoses, allergies, vitals, and medication history.

**US-031 (M) — Create SOAP note + diagnosis**
- Given an open encounter, When I record SOAP and select an ICD-10 diagnosis, Then it is saved to the EMR with author and timestamp.
- Given a required field missing, When I save, Then errors are shown and save is blocked.

**US-032 (M) — Create investigation/radiology order**
*As a Doctor, I want to order labs/imaging so providers can fulfil them.*
- Given a diagnosis/context, When I create an order, Then it enters Requested; if high-cost it routes to Approvals (PendingApproval), else becomes Active and available to authorized providers.
- Given an Active order, Then it is discoverable by the authorized provider only.

**US-033 (M) — Create e-prescription**
- Given an encounter, When I prescribe, Then a prescription is created (Draft→Submitted) with lines, quantities, and (future) interaction/allergy alerts surfaced.
- Given an expensive drug requiring approval, When I submit, Then it routes to Approvals before becoming dispensable.

**US-034 (S) — Create referral**
- Given a need for another provider, When I create a referral, Then it enters Requested and can be accepted/scheduled, closing the loop back to me on completion.

## Phase 5 — Lab & Imaging

**US-040 (M) — Provider finds authorized orders**
*As a Lab technician, I want to search active orders so I can fulfil them.*
- Given an order for my facility, When I search by patient identifier or order number, Then I see only lines authorized for my provider and cannot see prescriptions.
- Given an order not for me / already used, When I search, Then it does not appear as available.

**US-041 (M) — Atomically consume an order line**
*As a Lab technician, I want to consume an order line exactly once so duplicate usage is impossible.*
- Given an Active line, When I consume it, Then it is locked and marked consumed in a single atomic transaction.
- Given the line is already consumed, When I attempt to consume again, Then I get "already used" and no state changes (idempotent, duplicate-proof).
- Given I consume some of several lines, Then the order becomes PartiallyUsed and remaining lines stay Active.

**US-042 (M) — Upload result & complete**
- Given a consumed line, When I upload a result and attach a report, Then it is stored and routed to the ordering doctor/approvals.
- Given all lines fulfilled, Then the order becomes Completed.

## Phase 6 — Pharmacy

**US-050 (M) — Find a prescription**
*As a Pharmacist, I want to search prescriptions by Rx/Patient/Policy/Passport/Member so I can dispense.*
- Given a valid search, When it matches a dispensable Rx, Then I see its lines and remaining quantities and cannot see investigation results.
- Given an expired or completed Rx, When I open it, Then dispensing is rejected with the reason.

**US-051 (M) — Partial dispense with batch/expiry**
*As a Pharmacist, I want to dispense partially and record batch/expiry so records are accurate and the remainder stays available.*
- Given remaining quantity, When I dispense ≤ remaining with batch + expiry, Then a dispense event is recorded and remaining decremented.
- Given I dispense less than prescribed, Then the Rx becomes PartiallyDispensed and the remainder stays available for later.
- Given full dispensing, Then the Rx becomes Dispensed.

**US-052 (S) — Substitution / out-of-stock**
- Given an approved alternative, When I substitute, Then the substitution and reason are recorded.
- Given out-of-stock, When I flag it, Then the out-of-stock workflow is triggered and the beneficiary/prescriber can be notified.

## Phase 7 — Approvals

**US-060 (M) — Review & decide authorization**
*As a Medical Approval reviewer, I want to review requests with full clinical context so I can decide correctly.*
- Given a submitted request, When I open it, Then I can see EMR, clinical notes, and supporting documents.
- Given my decision, When I Approve/Reject/Partial/Request-info, Then reviewer, timestamp, decision, and rationale are recorded; rejection reason is mandatory.

**US-061 (S) — Emergency / override**
*As a Medical Approval reviewer, I want emergency approval with break-glass so urgent care isn't delayed.*
- Given an emergency, When I issue an emergency approval/override, Then extra justification is required and a break-glass audit entry is written.

**US-062 (S) — Manual authorization**
*As a Medical Approval reviewer, I want to create an authorization directly for a member without a provider submission.*
- Given a member search, When I create a manual authorization, Then it is valid, linked to the member, and fully audited.

## Cross-cutting

**US-070 (M) — Secure login with MFA**
*As any internal/provider user, I want SSO + MFA so access is secure.*
- Given valid credentials + MFA, When I sign in, Then I land on my role's portal only.
- Given inactivity beyond the timeout, When I return, Then I'm warned and re-authenticated.

**US-071 (M) — Role-scoped portal & data**
*As a user, I want to see only my portal and permitted data so minimization holds.*
- Given my role, When I navigate, Then I only see routes/data my permissions allow (e.g., Finance sees no diagnoses).
- Given a deep link I can't access, When I open it, Then I get a 403 with a request-access affordance, audited.

**US-072 (S) — Notifications & alerts**
- Given a relevant event (order available, approval decision, result ready), When it occurs, Then the right role receives an in-app (and email) notification.

**US-073 (S) — Operational dashboard**
*As a Medical Director/Manager, I want KPIs so I can monitor operations.*
- Given data, When I open the dashboard, Then I see clinic workload and key KPIs, each with an accessible data-table alternative.

**US-074 (M) — Manage users, roles & master data**
*As a Super Admin, I want to manage users/roles and master data so the platform stays correct.*
- Given admin rights, When I assign roles or update ICD/CPT/Drug master data, Then changes take effect and are audited; segregation-of-duties is enforced.

---

### Definition of Ready / Done
- **Ready:** story has role, value, AC, priority, dependencies, and links to the relevant FR/flow/state-machine.
- **Done:** AC pass, G-A11y + G-Audit met, tests (unit/integration/E2E as relevant) green, min-necessary verified, demoed.

### Cross-references
- Backlog: [31-product-backlog.md](31-product-backlog.md) · Functional reqs: [07-functional-requirements.md](07-functional-requirements.md)
- Flows: [13-ux-flows.md](13-ux-flows.md) · Lifecycles: [23-state-machines.md](23-state-machines.md) · Sprints: [33-sprint-roadmap.md](33-sprint-roadmap.md)
