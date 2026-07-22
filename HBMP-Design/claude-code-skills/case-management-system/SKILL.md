---
name: Case Management System
description: Designs Mersal HBMP case management and care coordination — the case entity, ABAC case-assignment access (unassignment revokes), beneficiary-360 scoped to assigned cases, coordination tasks, and escalations for complex/chronic/refugee beneficiaries. Use when building or reviewing case management, care coordination, or case-worker workflows.
---

# Case Management System

## Purpose
Give Case Managers a longitudinal, coordination-first workspace for a defined case load of
**complex, chronic, and vulnerable refugee** beneficiaries — "the thread": one view of eligibility,
care plan, open approvals, appointments, referrals, and the coordination-relevant clinical summary,
with access strictly bound to assignment (10 §3.11, personas C1/C2).

## When to use / when not to use
- **Use when:** building/reviewing the case entity, case assignment/unassignment, the beneficiary-360
  view, coordination tasks, escalations, care plans, and care coordination for complex/chronic/
  refugee cases; wiring the ABAC case-assignment access rule.
- **Do not use for:** the approval adjudication workflow (Case Managers *request/track*, they do not
  adjudicate); raw clinical authoring (that is the treating clinician); front-desk scheduling.

## Mersal domain knowledge & rules
- **Who / why:** Case Managers are care coordinators / social workers / complex-case managers. Their
  value is **continuity** — stitching a fragmented, multi-provider journey into one thread for chronic
  and newly-arrived refugee beneficiaries (personas Um Yusuf / Abdullah).
- **Case entity:** an open coordination record over a beneficiary — care plan, coordination tasks,
  open approvals, appointments, referrals, and linked encounters/summaries. Cases open, are tracked,
  escalate, and close.
- **Scope = `beneficiary:assigned` (HARD RULE):** a Case Manager sees **only** beneficiaries
  explicitly assigned to their case load. **Access follows assignment; unassignment revokes it**
  immediately. This is the ABAC `case-assignment` gate — analogous to the doctors'
  treating-relationship gate.
- **Beneficiary-360, but minimized:** the coordination view shows eligibility, care plan, open
  approvals, appointments, and **coordination-relevant clinical *summaries*** — not necessarily every
  raw lab/imaging result unless the care plan requires it (T3, scoped to case load). Refugee/SPI
  status is redacted by default and shown only with cause.
- **Coordination duties:** coordinate referrals across providers, liaise with the Approval team and
  providers, **raise/track medical-approval requests** (Case Manager can request; **SoD — cannot
  adjudicate**), manage care plans, and open/track cases/tickets (Call Center escalates here).
- **Escalations:** Case Managers are the escalation target for **repeated no-shows** (vulnerability vs.
  abuse — reviewed, never punitive) and for member-status actions such as suspend/reinstate (per
  ../../23 §1). Escalate clinical-governance matters to the Medical Director.
- **Member lifecycle touchpoints:** Case Managers can drive `Suspended → Active` (reinstate, issue
  cleared) and are actors in suspension review — always with mandatory reason + audit.

## Key entities, states & invariants
- Case: open/assigned → in-progress (tasks, escalations) → closed; assignment is the access primitive.
- Assignment invariant: **granting assignment grants scoped access; removing it revokes access** —
  no standing access to former case-load beneficiaries; every assignment change is audited.
- Coordination-relevant clinical read is **summary-level** by default; raw PHI only where the care
  plan justifies it, and every PHI read is audited (append-only, hash-chained).
- SoD: the requester of an approval cannot be its adjudicator; Case Managers request, Approval decides.
- Cross-links: referrals coordinated here follow the referral lifecycle (../../23 §4); approvals
  follow the authorization lifecycle (../../23 §5); eligibility comes from the eligibility service.

## How to apply
1. Assign a beneficiary to a Case Manager → opens/links the case and grants scoped 360 access.
2. Build the care plan; capture coordination tasks and their status; set escalation triggers.
3. Coordinate across the journey: referrals, appointments, and approval requests (request, not decide).
4. Handle escalations (repeated no-show review, suspension/reinstatement) with mandatory reason.
5. On resolution, close the case; on hand-off, reassign — access moves with the assignment.
6. Audit every assignment change and every PHI/summary read.

## Canonical references
- ../../10-role-matrix.md (§3.11 Case Managers; §2 scope vocabulary `beneficiary:assigned`; §7 SoD)
- ../../03-user-personas.md (Case Manager persona; C1 Abdullah refugee, C2 Um Yusuf chronic)
- ../../23-state-machines.md (§1 member lifecycle; §4 referral; §5 authorization — coordinated here)

## Guardrails
- Enforce `beneficiary:assigned` ABAC — no access to beneficiaries outside the case load; unassign revokes.
- Default to coordination-level summaries; pull raw PHI only when the care plan requires it; audit reads.
- Redact refugee/SPI status by default; surface only with documented cause.
- Case Managers request approvals but never adjudicate them (SoD).
- Escalations for repeated no-shows are review-first, never punitive; every escalation is audited.
