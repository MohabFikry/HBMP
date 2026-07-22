# Phase 12 — Data Migration, Release Management & Go-Live

**Goal:** Take the hardened platform (phase 11) into production **safely**: migrate existing **master data**, **providers**, and **beneficiaries** with reversible, audited, DPIA-cleared pipelines; finalize the **release-management** machinery (gated dev→QA→staging→prod promotion with progressive delivery and automated rollback); and execute a **pilot-first go-live** with training, cutover/fallback, and hypercare. This is the last phase — it turns a production-ready system into a **live, adopted, operable** one, gated by UAT + security + compliance sign-offs.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

---

## Skills to activate
> Activate `beneficiary-lifecycle-management`, `provider-network-management`, `ngo-healthcare-operations` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [../35-implementation-plan.md](../35-implementation-plan.md) — **§3 governance gates**, **§4 environments & release management**, **§5 data migration & onboarding** (streams + principles), **§6 training**, **§7 go-live & hypercare**, **§8 success metrics**.
- [../25-deployment-architecture.md](../25-deployment-architecture.md) — **§1 environments** (prod data never downstream unmasked), **§8 CI/CD** gates + progressive delivery + expand/contract migrations.
- [../0C-OPEN-SOURCE-STACK.md](../0C-OPEN-SOURCE-STACK.md) — free/open-source, on-prem-first, cloud-ready stack: k3s/Helm promotion, GitLab CE/Woodpecker + Harbor + Trivy CI/CD, OpenTofu/Ansible GitOps, and pgBackRest/Velero/restic backups + deployment tiers (Tier 1 single server → Tier 3 cloud).
- [../20-compliance-checklist.md](../20-compliance-checklist.md) — DPIA/RoPA, retention, PDPL residency, pre-prod compliance gate.
- [../18-security-model.md](../18-security-model.md) — pre-prod security gate; [../19-audit-strategy.md](../19-audit-strategy.md) — every migration mutation audited (hash-chained).
- [../11-permission-matrix.md](../11-permission-matrix.md) — provider/tenant isolation to verify post-migration; [../22-data-dictionary.md](../22-data-dictionary.md) / [../15-database-erd.md](../15-database-erd.md) — target schemas + field sensitivity for mapping.
- Depends on: phase 0b (master-data service + ingest), phase 1 (beneficiary/policy/coverage), provider-network phase (directory/contracts/users), and **phase 11 gates green** (perf, security sign-off, DR drill).
- Prerequisite environments: dry-runs happen in **staging with masked data**; go-live targets **production** only after gates pass.

---

## Prompts

### 12.1 — Reversible, audited data-migration pipelines (master data, providers, beneficiaries)

```text
Build migration loaders/pipelines to onboard existing data as first-class, reversible, audited streams. Read ../35 §5, ../22, ../15, ../20, ../19, ../11 first. Every load is idempotent, reversible, and fully audited. Dry-run in STAGING with MASKED data before any production run.

FRAMEWORK (shared)
- Create a /migration toolkit: staging tables -> validate -> transform/map -> load -> reconcile, driven by versioned config. Each run has a migration_batch_id; every inserted/updated row records provenance (source system, source id, batch id) and writes a hash-chained audit event (../19).
- REVERSIBILITY: every load has a tested rollback (by batch_id) that soft-deletes/reverts migrated rows without touching pre-existing data. Prove rollback in staging.
- IDEMPOTENCY: re-running a batch does not duplicate; use natural keys + source id + upsert semantics.

STREAM A — MASTER DATA (already ingested in phase 0b)
- Reconcile ICD-10/CPT/LOINC-ready + Drug/ATC + allergens/interactions already loaded via 0b: assert counts, versions, and a clinical spot-check. This stream VALIDATES, not re-loads, unless a version bump is needed.

STREAM B — PROVIDERS (directory / contracts / users)
- Import provider organizations, locations, contracts, and provider users from existing contracts/spreadsheets. Map to the provider-network schema; assign scopes/roles.
- VERIFY ISOLATION: after load, prove each provider user logs in scoped only to their own data (provider/tenant isolation, ../11) via automated checks — no cross-provider leakage.

STREAM C — BENEFICIARIES (identifier normalization + dedupe + coverage)
- Staged import of beneficiary records with: identifier NORMALIZATION (national ID / UNHCR / passport formats), DEDUPE (deterministic + fuzzy match with a review queue for ambiguous merges — never auto-merge low-confidence), and POLICY/COVERAGE assignment.
- Produce a dedupe report (auto-merged, queued-for-review, rejected) and require human sign-off on the review queue before promotion.

RECONCILIATION & DPIA
- For every stream, emit a reconciliation report: source count vs loaded vs rejected, field-level mapping coverage, and exception list with reasons. Migration is not "done" until reconciliation balances and exceptions are triaged.
- Author a DPIA for the migration itself (../20): lawful basis, data flows, minimization, residency (PDPL), retention, and the masking approach for lower environments. DPO signs before any production migration.

ACCEPTANCE (Given/When/Then)
- Given a masked staging dataset, When a stream loads, Then rows are created with provenance + audit events, reconciliation balances, and rollback-by-batch cleanly reverts it.
- Given provider data loaded, When a provider user logs in, Then they see only their own scope (isolation verified).
- Given beneficiary import, When identifiers collide, Then normalization + dedupe route ambiguous cases to a review queue and never auto-merge low-confidence pairs; a dedupe report is produced.
- Given the migration DPIA, When reviewed at the compliance gate, Then it is signed and the masking approach for downstream envs is confirmed.

Deliverables: /migration toolkit + per-stream config, reconciliation + dedupe reports, rollback scripts, migration DPIA in /docs/compliance. No unmasked prod data in staging.
```

### 12.2 — Release management & gated environment promotion

```text
Finalize the CI/CD promotion machinery so releases flow dev->QA->staging->prod with progressive delivery, automated rollback, and enforced compliance/security gates. Read ../25 §8, ../35 §3-§4, and ../0C-OPEN-SOURCE-STACK.md first. Do not bypass any gate. The stack is free/open-source, on-prem-first, cloud-ready: promotion runs on **k3s + Helm** via **GitOps (OpenTofu/Ansible + Helm)**; CI/CD on **GitLab CE (or Gitea + Woodpecker)** with **Harbor** registry and **Trivy** scanning. The same Helm charts target on-prem k3s (Tier 2) and cloud (Tier 3), and Tier 1 is a single-server Docker Compose deployment.

PROMOTION PIPELINE (dev -> QA -> staging -> prod)
- Formalize the ../25 §8 pipeline: PR -> build + unit/contract tests + SAST (Semgrep) + a11y + container scan (Trivy) -> sign image (cosign) -> push **Harbor** -> auto-deploy dev -> auto-deploy QA + integration/E2E -> staging (APPROVAL GATE + UAT/DAST/pen) -> prod (APPROVAL GATE + progressive rollout on k3s).
- Each stage is IaC-provisioned (OpenTofu/Ansible/Helm) and reproducible; images are signed and admission-controlled (only signed/scanned images; no `latest`).

PROGRESSIVE DELIVERY + AUTOMATED ROLLBACK
- Implement blue/green or canary deploy to prod on k3s (e.g., Argo Rollouts / Flagger with Linkerd) with automated rollback on SLO breach (error rate / latency / burn-rate) using the phase-11 SLOs. Prove rollback with a deliberately bad canary in staging: SLO breach -> auto-revert -> alert.

BACKWARD-COMPATIBLE DB MIGRATIONS (expand/contract)
- Enforce the expand/contract pattern: additive/expand migration deploys first and is backward-compatible with the running version; contract (drop/rename) only after the new version is fully rolled out. Migrations are versioned and gated; no destructive change in a single step. Add a CI check that flags non-backward-compatible schema changes.

GATE ENFORCEMENT (staging -> prod)
- Wire the ../35 governance gates as hard, non-skippable pipeline conditions before staging->prod: COMPLIANCE gate (DPIA + RoPA + retention configured + legal sign-off) and SECURITY gate (pen-test findings resolved, authz tests green, break-glass audited) — both consuming the phase-11 sign-off artifacts. A failed or missing gate blocks promotion; overrides require recorded steering-committee approval.

ACCEPTANCE (Given/When/Then)
- Given a change, When it promotes, Then it passes each stage's automated gates in order and cannot reach prod without the staging approval + green security/compliance gates.
- Given a canary that breaches an SLO, When detected, Then the deploy auto-rolls-back and alerts, with no manual step required.
- Given a schema change, When it is not backward-compatible, Then CI flags it and the expand/contract split is required.
- Given a missing DPIA or unresolved Critical security finding, When staging->prod is attempted, Then promotion is blocked.

Deliverables: finalized pipeline-as-code, canary/blue-green config + rollback automation, expand/contract migration CI check, gate-enforcement config referencing phase-11 evidence.
```

### 12.3 — Pilot go-live, training & hypercare

```text
Execute a pilot-first go-live with training, a cutover/fallback plan, hypercare, and success metrics wired to dashboards. Read ../35 §6-§8, ../25 §9, and ../0C-OPEN-SOURCE-STACK.md first. Go live in PRODUCTION only after UAT + security + compliance gates pass.

PILOT SCOPE (one site, end-to-end)
- Launch at ONE clinic covering the full slice: reception/registration -> eligibility -> encounter/EMR -> order + e-prescription -> lab + imaging fulfillment -> pharmacy dispensing -> approvals. Validate the walking-skeleton end-to-end in production with real users before any second site (reduces risk vs big-bang).
- Target the **on-prem Tier 1 footprint** for the pilot: a single on-prem server running the stack via Docker Compose (or a single-node k3s), $0 licensing — the same Helm charts scale out to Tier 2 (multi-node k3s) / Tier 3 (cloud) later without re-platforming.

BACKUPS VALIDATED BEFORE GO-LIVE
- Before enabling users, prove backups + restore work: **pgBackRest** PITR for PostgreSQL, **Velero** for k3s cluster/PV state, and **restic** for object/file data — with at least one offsite copy. A tested restore drill (not just a backup job) is a hard go/no-go item.

TRAINING MATERIALS (AR/EN)
- Produce role-based training (bilingual AR/EN) for each portal used in the pilot: Reception, Registration, Clinician, Nurse, Lab/Imaging, Pharmacy, Approvals (+ Case/Finance/Admin as relevant) — quick-start guides + short task videos, published to the knowledge base.
- Produce a PROVIDER ONBOARDING KIT: quick-start guides + short videos for external labs, imaging centers, and pharmacies (order queue, atomic consume, result upload, dispensing). Identify champions/super-users per team.

CUTOVER & FALLBACK
- Write a cutover runbook: sequence (final masked-data dry-run -> production migration run for the pilot cohort -> reconcile -> smoke test -> enable users), go/no-go checklist, and a timed schedule.
- Write a FALLBACK/rollback procedure including a MANUAL PAPER fallback kept available until adoption is stable, with a documented trigger and a data-catch-up plan for anything recorded on paper during a fallback.

HYPERCARE (2-4 weeks)
- Stand up hypercare: elevated monitoring (tighter alert thresholds, dashboard watch), daily incident triage, a fast-fix pipeline, on-call SRE, and a week-1 war-room. Reuse phase-11 runbooks; log every incident in the incident register and feed fixes/risks back (../27).

SUCCESS METRICS -> DASHBOARDS (../35 §8)
- Wire pilot success metrics to the reporting/observability dashboards: adoption (% visits processed digitally, active users per role, paper reduction), efficiency (eligibility check time, registration time, approval TAT, no-show rate), quality/safety (% encounters with structured diagnosis, duplicate-order rate ~0, audit completeness), reliability/security (SLO attainment, zero unresolved criticals, DR drill status).
- Define HYPERCARE EXIT CRITERIA as explicit thresholds (incident rate below X, TAT within SLO, adoption above Y) and gate the clinic-by-clinic rollout on meeting them.

ACCEPTANCE (Given/When/Then)
- Given the pilot clinic, When go-live executes via a gated release, Then the full slice works end-to-end in production and any regression can be rolled back (incl. manual paper fallback).
- Given each portal + provider type, When staff onboard, Then bilingual AR/EN training + the provider onboarding kit are available and champions identified.
- Given the pilot runs, When metrics accrue, Then adoption/efficiency/quality/reliability KPIs render on dashboards and are measured against exit thresholds.
- Given hypercare, When the exit criteria are met, Then rollout to the next clinic is authorized; otherwise it holds.

Deliverables: cutover + fallback runbooks, bilingual training set + provider onboarding kit, hypercare plan + incident register, success-metrics dashboards + documented exit criteria.
```

---

## Guardrails

- **Prod data never flows downstream unmasked.** All dry-runs and rehearsals use masked/synthetic data in staging; the migration DPIA documents the masking approach for lower environments (../25 §1, ../20).
- **Every migration is reversible, audited, and DPIA-cleared.** No production load without a tested rollback-by-batch, balanced reconciliation, provenance on every row, hash-chained audit (../19), and a signed migration DPIA. Low-confidence dedupe merges are never automatic.
- **Isolation preserved.** Post-migration, provider/tenant isolation and minimum-necessary scoping are verified by automated checks (../11) before users are enabled.
- **Go-live only through gates.** Production release requires UAT sign-off + green security gate + green compliance gate + a passed DR drill (from phase 11); gates are non-skippable and overrides need recorded steering-committee approval (../35 §3).
- **Pilot-first, reversible.** No big-bang: one clinic proves the end-to-end slice before rollout, with progressive delivery + automated rollback and a manual fallback retained until adoption stabilizes.

## Done when

- **Master data, providers, and beneficiaries are migrated and reconciled in staging** via reversible, audited, idempotent pipelines: reconciliation balances, dedupe/review-queue signed off, provider isolation verified, and the migration DPIA is signed.
- The **promotion pipeline enforces gated dev→QA→staging→prod** with progressive delivery (blue/green or canary), proven automated rollback on SLO breach, expand/contract backward-compatible migrations, and non-skippable security/compliance gates consuming phase-11 evidence.
- A **pilot clinic goes live in production via a gated release** — full slice working end-to-end — with a cutover/fallback plan (including manual paper fallback) and demonstrated rollback.
- **Hypercare is running** (elevated monitoring, daily triage, war-room, runbooks, incident register) and **success metrics are wired to dashboards** with explicit, measured hypercare exit criteria gating clinic-by-clinic rollout.
- All go-live gate conditions (UAT + security + compliance + DR) are green and recorded. Global Definition of Done met.
