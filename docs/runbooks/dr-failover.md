# Runbook: DR failover to second site

- **Trigger:** primary-site outage (multi-node/pod-kill beyond auto-recovery, or site loss). Manual invocation by incident commander.
- **Impact:** platform unavailable at primary; clinical operations halted until failover. Targets: **RPO ≤ 15 min, RTO ≤ 2 h** (NFR-071/072).
- **Owner / on-call:** platform on-call → incident commander → DPO (PHI continuity).

## Diagnosis checklist
1. Confirm primary is truly down (not a network partition) — check Prometheus federation / external probe.
2. Confirm the second-site Patroni replica is healthy and its replication lag < RPO (15 min).
3. Confirm the offsite MinIO copy and latest pgBackRest WAL are current.

## Recovery steps (per `25-deployment-architecture.md` §9)
1. **Promote** the second-site Patroni/PostgreSQL replica to primary (pgBackRest PITR as fallback if replica is unhealthy).
2. **Restore MinIO** object store from the offsite copy (restic); confirm object-lock/WORM retained.
3. **Redeploy services** to the second site via Helm/IaC (Velero for cluster state + volumes).
4. **Repoint DNS/ingress** to the second site; validate TLS + ModSecurity active.
5. Smoke-test the golden paths (login, reception search, eligibility, worklist, consume).

## Verification
- Measure **data loss** (last committed txn vs promoted state) ≤ 15 min RPO.
- Measure **time-to-service** (outage start → golden-path green) ≤ 2 h RTO.
- Run `infra/dr/restore-rehearsal.sh`-style reconciliation on the promoted DB; confirm the
  **audit hash-chain linkage is intact** (NFR-120/123) — WORM survived the failover.
- File the signed drill report (`docs/runbooks/dr-drill-report.md`).

## Post-incident
- Record start/end timestamps, measured RPO/RTO, and any manual steps to automate.

## Escalation
- Incident commander owns the call to fail over; DPO signs off on PHI continuity; engineering lead on promotion issues.
