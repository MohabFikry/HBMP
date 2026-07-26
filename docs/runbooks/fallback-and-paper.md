# Runbook: Fallback & Manual Paper Procedure

Phase 12.3. How to revert the pilot safely, including the **manual paper fallback** retained until
adoption is stable, and the **data catch-up** for anything recorded on paper during a fallback
(../35 §7). Companion: [`cutover-and-golive.md`](cutover-and-golive.md),
[`deploy-and-rollback.md`](deploy-and-rollback.md).

- **Trigger (any one):**
  - Sev-1 that blocks the clinical slice (can't register / can't dispense / can't approve) > 15 min.
  - Data-integrity or PHI-exposure incident.
  - Go/no-go item discovered failed after enable.
  - SLO breach that auto-rollback did not resolve.
- **Impact:** pilot clinic reverts to previous release or to paper; no other clinic affected.
- **Owner / on-call:** SRE (R), Clinical champion (paper), PO (A), Security/DPO (if PHI).

## Decision: software rollback vs paper fallback
| Situation | Action |
|---|---|
| Bad release, data intact | **Software rollback** (previous image tag / previous ReplicaSet) |
| Migration produced bad data | `mersal-migrate rollback --batch <id>` (soft-revert) + re-run corrected |
| Platform unavailable / infra down / integrity doubt | **Manual paper fallback** |

## A. Software rollback (fast path)
1. Tier 2/3: `kubectl argo rollouts undo hbmp-<svc>` (revert to the stable ReplicaSet), or re-point
   Helm to the previous git-SHA image tag. Tier 1: `deploy-and-rollback.md` (compose previous tag).
2. Reverse the pilot migration if it introduced the fault: `mersal-migrate rollback --batch <id>`
   (soft-reverts exactly that batch; pre-existing rows untouched).
3. Verify: smoke test the slice; dashboards back under SLO; audit chain intact.

## B. Manual paper fallback (clinical continuity)
1. **Declare fallback** — clinical champion announces; switch the pilot clinic to the printed
   paper kits (registration form, eligibility slip, encounter sheet, order/e-Rx pad, dispense log,
   approval request). Kits live on-site (printed at cutover).
2. Keep **every paper record numbered** and time-stamped; the champion keeps a fallback log
   (start time, patients seen on paper, forms used).
3. Care continues on paper; no data is lost because it is captured on the numbered forms.

## Data catch-up (after service is restored)
1. Restore the platform (software rollback done, or fix deployed).
2. **Back-enter** the numbered paper records into HBMP through the normal portals, stamping the
   real event time (not the entry time) where the forms support it.
3. Reconcile: count paper forms vs records entered; the fallback log is the source count. Any
   unresolved form is an exception to triage — the catch-up is not "done" until it balances.
4. Every back-entered record is audited as normal (actor = the entering user); note "paper-catchup"
   in the encounter/interaction note for traceability.

## Verification
- Slice works again end-to-end; fallback log reconciles to entered records (balanced).
- No orphaned paper forms; audit chain intact; DPO notified if any PHI exposure occurred.

## Post-incident
- Timeline + root cause; feed fixes/risks to the backlog + incident register
  ([`incident-register.md`](incident-register.md)); ADR if a design change is needed.
- Do not exit hypercare until the fallback cause is resolved and catch-up balances.

## Escalation
Clinical champion (paper continuity) + SRE (restore) → engineering owner → PO → Security/DPO →
steering committee.
