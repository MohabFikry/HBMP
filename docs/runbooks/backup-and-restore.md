# Runbook: backup & restore

- **Trigger:** scheduled (daily full + continuous WAL); or on-demand for a restore test / recovery.
- **Impact:** none for backup; restore targets a scratch/recovery instance (never overwrites a live primary blindly).
- **Owner / on-call:** platform on-call.

## Backups (verify healthy)
- **pgBackRest**: daily full + continuous WAL (PITR). Confirm `pgbackrest info` shows a recent full + WAL archive lag ≈ 0 (NFR-070).
- **Velero**: cluster state + PV snapshots. **restic**: files + MinIO objects, with an **offsite/second-site copy** (NFR-076).

## Restore test (quarterly drill + pre-release)
1. Run `infra/dr/restore-rehearsal.sh` (dev/compose) or the pgBackRest restore-to-scratch flow (staging).
2. It reconciles **exact per-table row counts** source vs restored and verifies the **audit
   hash-chain linkage** is intact on the restored copy.
3. Record evidence (counts, timestamps, pass/fail) — feeds the DR drill report + restore evidence.

## Verification
- Row counts reconcile exactly; audit chain shows 0 linkage violations; MinIO object-lock retained.

## Post-incident / cadence
- Schedule restore drills quarterly; any reconciliation mismatch is a release blocker until root-caused.

## Escalation
- Backup failure or reconciliation mismatch → platform lead; suspected data loss → DPO.
