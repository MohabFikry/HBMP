# Runbook: deploy & rollback

- **Trigger:** release deploy; or `LatencySLOBreachPrimaryScreens` / `ServiceSaturationCPU` / error spike after a deploy.
- **Impact:** a bad deploy can breach latency/error SLOs; rollback restores the last-good release.
- **Owner / on-call:** release engineer + service owner.

## Deploy (expand/contract, backward-compatible)
1. CI gates green: build, tests, `security-ci` (no Critical/High, no secret), `perf-ci` smoke.
2. **Apply schema DDL first** (see below). Migrations are **expand/contract** (additive first), so the new
   schema is compatible with the running release and old + new pods can serve together.
3. Helm/IaC deploy.
4. Roll pods; watch golden-signal dashboard for the service (`mersal-golden-signals`).

### Applying schema DDL — by hand, in order
**There is no migration runner and no ledger table.** Nothing applies these files and nothing records that
they were applied — `tools/migration` is the *data* onboarding toolkit (beneficiaries/providers/master data),
not DDL. If a step below is skipped, the release deploys against an older schema and fails at runtime, not at
deploy time.

For each service whose `Infrastructure/Migrations/` changed in the release, apply every not-yet-applied file
**in filename order** (they are numbered `0001_`, `0002_`, … and are order-dependent):

```sh
psql -h <host> -p <port> -U <user> -d <db> -v ON_ERROR_STOP=1 \
  -f services/<service>/Infrastructure/Migrations/<NNNN>_<name>.sql
```

- `ON_ERROR_STOP=1` is not optional: without it psql continues past a failed statement and reports success on
  a half-applied file.
- **Rehearse on a scratch restore first.** With no ledger, "has this already been applied?" is answered by
  inspecting the schema (`\d+ <schema>.<table>`), not by a query — so confirm the intended end state before
  touching a live database.
- Most files are written to be re-runnable (`IF NOT EXISTS` / `DROP … IF EXISTS`), but **not all**, and a file
  can guard one statement and not the next. Re-running is safe only after reading that specific file — there
  is no blanket idempotency guarantee to rely on.
- Wrap in `BEGIN; … ROLLBACK;` to validate syntax and permissions against the real database without
  committing, then re-run for real.

Record which files were applied where, in the release notes, since the database will not remember for you.

## Rollback
1. `helm rollback <release> <prev-revision>` (or redeploy the prior image tag).
2. Migrations were expand-only, so no down-migration needed; if a contract step already ran, restore from PITR to just-before (rare).
   Leave the applied DDL in place — that is what "expand first" buys: the previous release runs against the
   new schema. Reversing the DDL to match the rolled-back code is the failure mode this ordering avoids.
3. Confirm latency/error back under SLO; announce resolution.

## Verification
- p95 under NFR-001 (1.5 s), 5xx ratio back to baseline, no alert firing.

## Post-incident
- Note the offending change; add a regression test/perf threshold so it can't recur silently.

## Escalation
- Service owner → engineering lead. Data-affecting rollback → platform lead + DPO.
