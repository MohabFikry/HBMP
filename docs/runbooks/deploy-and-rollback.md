# Runbook: deploy & rollback

- **Trigger:** release deploy; or `LatencySLOBreachPrimaryScreens` / `ServiceSaturationCPU` / error spike after a deploy.
- **Impact:** a bad deploy can breach latency/error SLOs; rollback restores the last-good release.
- **Owner / on-call:** release engineer + service owner.

## Deploy (expand/contract, backward-compatible)
1. CI gates green: build, tests, `security-ci` (no Critical/High, no secret), `perf-ci` smoke.
2. Helm/IaC deploy; DB migrations are **expand/contract** (additive first) so old + new run together.
3. Roll pods; watch golden-signal dashboard for the service (`mersal-golden-signals`).

## Rollback
1. `helm rollback <release> <prev-revision>` (or redeploy the prior image tag).
2. Migrations were expand-only, so no down-migration needed; if a contract step already ran, restore from PITR to just-before (rare).
3. Confirm latency/error back under SLO; announce resolution.

## Verification
- p95 under NFR-001 (1.5 s), 5xx ratio back to baseline, no alert firing.

## Post-incident
- Note the offending change; add a regression test/perf threshold so it can't recur silently.

## Escalation
- Service owner → engineering lead. Data-affecting rollback → platform lead + DPO.
