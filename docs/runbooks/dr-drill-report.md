# DR Drill & Restore Evidence Report — Mersal HBMP (Phase 11.3)

The headline reliability gate. Two parts: (A) a **restore rehearsal** (runnable now, evidence
below) and (B) a **full failover drill to the second site** (requires the target k3s + second
site — method + acceptance recorded, execution pending that infra). Targets: **RPO ≤ 15 min,
RTO ≤ 2 h** (NFR-071/072); audit WORM/hash-chain survives failover (NFR-120/123).

---

## Part A — Restore rehearsal (executed)

Tool: `infra/dr/restore-rehearsal.sh`. Method: `pg_dump` the source → restore to a scratch DB →
reconcile **exact per-table `count(*)`** source vs restored → verify **audit hash-chain
linkage** on the restored copy (`prev_hash` continuity per `partition_key`/`seq`).

| Field | Value |
|---|---|
| Date | 2026-07-26 |
| Environment | Dev (compose Postgres 16, host :55432) |
| Source DB | `hbmp` |
| Scratch DB | `hbmp_restore_check` (created + dropped by the tool) |
| Tables reconciled | **138** across **18** service schemas (admin, approvals, audit, callcentre, case, claims, document, eligibility, emr, finance, masterdata, notification, orders, patient, pharmacy, policy, provider, reporting) |
| Row-count reconciliation | ✅ **exact match** source vs restored (all 138 tables) |
| Audit hash-chain linkage | ✅ **0 linkage violations** on the restored copy |
| Result | ✅ **PASS** |

> Honest note on the chain check: in the dev DB the `audit.audit_event` table is currently
> empty, so the linkage traversal passed with 0 rows (a **structural** pass — the query is
> valid and runs on the restored copy). A volume-seeded staging run exercises the full
> traversal over real chained rows; that run's output attaches here at staging time.
> Cryptographic **recomputation** of `record_hash` (vs. linkage continuity) is a separate
> app-level check using the audit-service hashing routine — tracked as a follow-up.

## Part B — Full failover drill to second site (pending target infra)

Method per `25-deployment-architecture.md` §9 and `docs/runbooks/dr-failover.md`:
promote second-site Patroni replica (pgBackRest PITR fallback) → restore MinIO from offsite
(restic) → Helm/IaC redeploy (Velero) → repoint DNS/ingress → smoke golden paths.

| Measure | Target | Result |
|---|---|---|
| Data loss (RPO) | ≤ 15 min (NFR-071) | PENDING (needs 2nd site) |
| Time-to-service (RTO) | ≤ 2 h (NFR-072) | PENDING |
| Audit WORM/object-lock survived + chain intact | required (NFR-120/123) | PENDING (rehearsal proves the reconciliation method) |
| Golden paths green post-failover | required | PENDING |

## Sign-off
| Role | Name | Date | Result |
|---|---|---|---|
| Platform / SRE owner | | 2026-07-26 | Part A PASS; Part B pending target infra |
| DPO (PHI continuity) | | | |
