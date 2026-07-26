# Runbook: approvals backlog / TAT breach

- **Trigger:** `ApprovalsSLABreach` alert (`approvals_pending_over_sla > 0`) or TAT p95 climbing.
- **Impact:** authorizations delayed → downstream orders/prescriptions gated → patient care delay.
- **Owner / on-call:** approvals ops lead + clinical duty manager.

## Diagnosis checklist
1. Business KPI dashboard (`mersal-business-kpis`): pending count, over-SLA count, TAT p95.
2. Is it demand (spike in submissions) or capacity (reviewers offline / worklist stuck)?
3. Check approvals-service health + event-bus backlog (a stuck consumer can starve the worklist).

## Recovery steps
1. Surge staffing / reassign worklist items (respecting SoD — no self-approval).
2. If a consumer is stuck, see `event-bus-backlog.md`; replay if needed.
3. For genuine emergencies, break-glass is available (time-boxed, justified, audited) — do NOT use it to bypass SLA routinely.

## Verification
- Over-SLA count → 0; TAT p95 back under target; no downstream gate backlog.

## Post-incident
- Right-size reviewer capacity / SLA thresholds; capture demand pattern for capacity planning.

## Escalation
- Ops lead → clinical duty manager → medical director (for override authority).
