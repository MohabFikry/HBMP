# Go-Live Gate — Mersal HBMP (Phase 12)

The single, non-skippable gate the `release.yml` pipeline enforces before **staging → production**
(../../HBMP-Design/35-implementation-plan.md §3, `25-deployment-architecture.md` §8). Each gate is
either **automated** (a CI check that blocks) or a **recorded human sign-off**. A missing artifact or
an unresolved Critical finding **blocks promotion**; an override requires a **recorded
steering-committee approval** (not a code change).

Automation: `tools/ci/check-golive-gates.py` (`--require-signed` in the prod job) evaluates the
gates below and exits non-zero until they are all green.

| Gate | Evidence artifact | Enforced by | Status source |
|---|---|---|---|
| **SECURITY** | `docs/compliance/security-sign-off.md` — pen-test High/Crit resolved, authz suites green, break-glass audited | Security/DPO signature + `security-ci.yml` (SAST/SCA/image/DAST) | signed row check |
| **COMPLIANCE / DPIA** | `docs/compliance/migration-dpia.md` — lawful basis, minimization, PDPL residency, retention, masking | DPO signature | signed row check |
| **DR** | `docs/runbooks/dr-drill-report.md` — restore/PITR drill **PASS** | phase-11 restore-rehearsal (`infra/dr/restore-rehearsal.sh`) | "PASS" token |
| **PERF** | `docs/PERFORMANCE-BASELINE.md` — NFR §1/§2 thresholds measured | phase-11 `perf-ci.yml` (k6) | present |
| **MIGRATION-COMPAT** | expand/contract clean | `tools/ci/check-migration-compat.py --all` | exit code |
| **UAT** | UAT sign-off recorded on the staging deploy | steering committee | recorded on the Environment approval |
| **PROGRESSIVE-ROLLBACK** | staging bad-canary drill auto-reverted | `infra/helm/rollout/` Argo Rollouts drill | drill report |

## How the pipeline enforces it
1. `build-verify` — full backend + frontend gates, SAST (Semgrep), Trivy fs/config/secret (block Crit/High).
2. `images` — per-service build → Trivy image scan → **cosign sign** → push **Harbor**; admission
   control admits only signed+scanned images, **never `:latest`**.
3. `deploy-dev` → `deploy-qa` (auto) → `deploy-staging` (**required-reviewer approval** + DAST + masked
   migration dry-run).
4. `govern-gates` — `check-golive-gates.py --require-signed` (**hard stop** until SECURITY + COMPLIANCE
   are signed and DR/PERF/MIGRATION are green).
5. `deploy-prod` (**required-reviewer approval = recorded steering-committee sign-off**) — expand
   migrations first, then **Argo Rollouts canary** with SLO analysis auto-rollback, then smoke +
   hypercare handoff.

## Expand/contract discipline
Backward-compatible (expand) migrations deploy **with** the new version; destructive (contract)
migrations deploy **only after** the new version is fully rolled out. `check-migration-compat.py`
fails CI on a contract-phase operation inside an expand migration unless it is explicitly
acknowledged (`-- migrate-compat: contract-ok (reason)`) as the post-rollout contract step. The
current repo is **clean across all 83 migrations**.

## Current status (this repo, pre-go-live)
Running `check-golive-gates.py` today: SECURITY + COMPLIANCE = **PENDING (unsigned)** — the sign-off
blocks are wired and awaiting the DPO/Security signatures that happen at the gate meeting; DR, PERF,
and MIGRATION-COMPAT = **GREEN**. This is the expected state: the machinery is complete and the prod
job is correctly **blocked** until the human sign-offs land against the target infrastructure.
