# Runbook: Hypercare (Pilot, 2–4 weeks)

Phase 12.3. Elevated support after pilot go-live until the exit criteria are met, then clinic-by-clinic
rollout is authorized (../35 §7-§8). Companions:
[`cutover-and-golive.md`](cutover-and-golive.md),
[`incident-register.md`](incident-register.md), all phase-11 runbooks.

- **Trigger:** pilot go-live enabled (T-0 in the cutover runbook).
- **Impact:** heightened monitoring + fast-fix for the pilot clinic; rollout gated on exit criteria.
- **Owner / on-call:** SRE (on-call rota), PO (A), Clinical champion, Security/DPO on call.

## Structure
- **Week 1 war-room:** SRE + engineering + clinical champion co-located (virtual OK); daily stand-up.
- **Daily incident triage:** review the incident register; classify Sev-1..4; assign owners.
- **Fast-fix pipeline:** hotfix branch → the normal gated `release.yml` (gates are NOT bypassed) →
  canary to the pilot → verify. No direct-to-prod.
- **On-call SRE:** 24×7 for week 1, business-hours + on-call after, per the rota.

## Elevated monitoring (tighter thresholds than steady-state)
Watch the golden-signal + business-KPI Grafana dashboards + the **pilot go-live** dashboard.
Tighten alerting for the pilot window:
| Signal | Steady-state | Hypercare |
|---|---|---|
| API success rate | ≥ 99% | ≥ 99.5% (page on < 99.5% for 5m) |
| p99 latency (read) | ≤ 1s | ≤ 0.8s (warn), page > 1s |
| Failed consume/dispense | alert | **page immediately** |
| Approvals SLA/TAT | dashboard | page on breach |
| Auth anomaly / audit-chain | alert | page immediately |
Reuse the phase-11 alert rules (`infra/compose/config/rules/`) with the pilot thresholds.

## Incident handling
Every incident → the register (`incident-register.md`): id, time, severity, symptom, root cause,
fix, follow-up. Sev-1 → war-room + consider fallback (`fallback-and-paper.md`). Feed every fix and
risk back into the backlog + risk register (../27).

## Success metrics (wired to dashboards, ../35 §8)
Tracked live on the **pilot go-live** dashboard:
- **Adoption:** % visits processed digitally, active users per role, paper reduction.
- **Efficiency:** eligibility-check time, registration time, approval TAT, no-show rate.
- **Quality/safety:** % encounters with structured diagnosis, duplicate-order rate (~0), audit
  completeness.
- **Reliability/security:** SLO attainment, zero unresolved Criticals, DR drill status.

## Hypercare EXIT CRITERIA (explicit; gate the next clinic)
Rollout to clinic #2 is authorized **only when all hold for 5 consecutive business days**:
| Metric | Threshold |
|---|---|
| Sev-1 incidents | **0 open**, none in the last 5 days |
| Sev-2 incident rate | ≤ 1 / day, all with owners |
| API success rate (SLO) | ≥ 99.5% sustained |
| Approval TAT | within SLO |
| Duplicate-order rate | ~0 |
| Audit completeness | 100% of mutations audited, chain intact |
| Adoption | ≥ 80% of pilot visits processed digitally |
| Unresolved Critical security findings | 0 |
| Backups/restore | last restore drill green |
If any criterion fails, hypercare **holds** and rollout does not proceed.

## Escalation
SRE on-call → engineering owner → PO → Security/DPO (PHI/breach) → steering committee (rollout
authorization / hold decision, recorded).
