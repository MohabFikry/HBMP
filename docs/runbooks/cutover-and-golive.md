# Runbook: Pilot Cutover & Go-Live

Phase 12.3. The timed, gated sequence to take **one pilot clinic** live end-to-end in production.
Pilot-first, reversible, no big-bang (../35 §7). Companions:
[`fallback-and-paper.md`](fallback-and-paper.md), [`hypercare.md`](hypercare.md),
[`deploy-and-rollback.md`](deploy-and-rollback.md),
[`../compliance/go-live-gate.md`](../compliance/go-live-gate.md).

- **Trigger:** scheduled pilot go-live, after the go-live gate is green.
- **Impact:** the pilot clinic switches from paper/legacy to HBMP for the full slice
  (reception → eligibility → encounter/EMR → order + e-prescription → lab/imaging → pharmacy →
  approvals). Other clinics unaffected (rollout is clinic-by-clinic).
- **Owner / on-call:** SRE (R), PO (A), Clinical champion + Security/DPO on call.

## Pilot scope (one site, end-to-end)
One clinic on the **on-prem Tier-1 footprint** (single server, Docker Compose or single-node k3s,
$0 licensing). The same Helm charts scale to Tier 2/3 later without re-platforming.

## Go / No-Go checklist (T-minus 1 day)
All must be **YES** or go-live holds:
- [ ] Go-live gate green — `tools/ci/check-golive-gates.py --require-signed` passes (SECURITY +
      COMPLIANCE signed; DR/PERF/MIGRATION green). UAT signed off.
- [ ] **Backups + restore proven** (hard gate — see below).
- [ ] Final **masked-data dry-run** in staging balanced; rollback-by-batch rehearsed.
- [ ] **Schema DDL rehearsed on a scratch restore** and the outstanding file list written down — there is no
      migration runner and no ledger, so "which have been applied?" has no query that answers it. The data
      migration at T-30m writes into these tables and will fail, or land in the wrong shape, if any are missing.
- [ ] Migration DPIA signed; provider isolation verified on staging.
- [ ] Persistent issuer keys present (OpenBao RS256; issuer starts, JWKS stable).
- [ ] Progressive-rollback drill passed on staging (bad canary auto-reverted).
- [ ] Training delivered for the pilot roles; champions identified; provider onboarding kit sent.
- [ ] Manual **paper fallback** kits printed and on-site; fallback trigger + catch-up plan briefed.
- [ ] War-room scheduled; on-call rota published; hypercare dashboards up.

## Backups validated BEFORE enabling users (hard go/no-go)
Prove restore, not just backup:
1. **pgBackRest** PITR restore of the production DB to a scratch target → row-count + audit
   hash-chain reconcile (reuse `infra/dr/restore-rehearsal.sh`).
2. **Velero** restore of k3s cluster/PV state (Tier 2+) to a scratch namespace.
3. **restic** restore of MinIO object/file data; verify a document opens.
4. Confirm **≥ 1 offsite copy** exists and is restorable.
> If any restore fails → **NO-GO**.

## Cutover sequence (timed)
| T | Step | Owner | Verify |
|---|---|---|---|
| T-60m | Freeze legacy writes for the pilot cohort; announce start | PO | legacy read-only |
| T-45m | Final **masked** dry-run in staging; confirm reconciliation balances | SRE | balances=true |
| T-35m | **Apply service schema DDL** — every outstanding `services/*/Infrastructure/Migrations/*.sql`, in filename order, per [`deploy-and-rollback.md`](deploy-and-rollback.md#applying-schema-ddl--by-hand-in-order) | SRE | each file exits 0; target objects present via `\d+` |
| T-30m | **Production migration run** for the pilot cohort (`mersal-migrate run-* --env production --i-understand-prod`) | SRE | provenance + audit written |
| T-20m | **Reconcile**: source vs loaded/held/rejected; triage exceptions; sign off dedupe review queue | Data owner | balances + queue signed |
| T-15m | Verify provider isolation on prod (no cross-provider leakage) | Security | isolated=true |
| T-10m | **Smoke test** the full slice with a synthetic patient | QA | each stage green |
| T-0 | **Enable users** for the pilot clinic (flip access) | SRE | logins succeed, portals load |
| T+0 | Enter **hypercare**; open war-room | SRE/PO | dashboards live |

## Verification (live)
- A real end-to-end visit completes: register → eligibility → encounter → order + e-Rx → lab/imaging
  fulfilment → pharmacy dispense → approval — each step audited, min-necessary enforced.
- Golden-signal + business-KPI dashboards render pilot data; no Critical alerts firing.
- Audit hash-chain intact; no auth anomalies.

## Post-incident / rollback
Any go/no-go item failing, or a Sev-1 in the first hours → invoke
[`fallback-and-paper.md`](fallback-and-paper.md): re-point to the previous release (or paper), then
`mersal-migrate rollback --batch <id>` to reverse the pilot migration cleanly.

## Escalation
SRE on-call → engineering owner → PO → Security/DPO (any PHI exposure or suspected breach) →
steering committee (go/no-go override, recorded).
