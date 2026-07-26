# Data Protection Impact Assessment — Data Migration (Phase 12.1)

**Scope:** the one-off + incremental onboarding of existing **master data**, **provider**, and
**beneficiary** records into Mersal HBMP via the `tools/migration` toolkit.
**Status:** Draft for the compliance gate (../35 §3). **DPO signs before any production migration run.**
**Companions:** `20-compliance-checklist.md`, `18-security-model.md`, `19-audit-strategy.md`,
`25-deployment-architecture.md` §1, this repo's `docs/compliance/security-sign-off.md`.

Legend: ✅ implemented + verifiable in this repo · 🟡 operational gate (needs staging/prod infra or a
human sign-off).

---

## 1. Purpose & lawful basis
Mersal HBMP becomes the single source of truth for benefit administration and care delivery for
refugee beneficiaries. Migrating existing records (paper/legacy registries, provider contracts,
master terminologies) is necessary to operate the platform and deliver care.

- **Lawful basis:** provision of healthcare/benefit administration to data subjects Mersal already
  serves; processing is necessary for the charity's care mission and is limited to that purpose.
- **Special-category data:** beneficiary health/benefit data is sensitive. Minimization and
  need-to-know (min-necessary field projections) apply during and after migration exactly as in the
  running system (`18-security-model.md`).

## 2. Data flows
| Stream | Source | Sink | Sensitivity |
|---|---|---|---|
| Master data | ICD-10/CPT/LOINC-ready, Drug/ATC, allergens/interactions (already ingested, phase 0b) | validated in place (no PHI) | none |
| Providers | provider contracts/spreadsheets → provider orgs/locations/contracts/users | `migration.landing` → provider-network schema | commercial + user identities |
| Beneficiaries | legacy beneficiary registry/paper | `migration.landing` → patient/policy/coverage schemas | **PHI/PII (special category)** |

All loads flow through the toolkit's `staging tables → validate → transform/map → load → reconcile`
pipeline. Every landed row carries **provenance** (source system, source id, batch id) ✅.

## 3. Minimization & mapping
- Only the fields the platform needs are mapped (versioned per-stream `StreamConfig`); unmapped
  source columns are not carried over ✅.
- Field-mapping **coverage** is measured and reported per run (reconciliation report) ✅.
- Beneficiary identifiers are **normalized** (national ID / UNHCR / passport) to a canonical form so
  dedupe and idempotent upsert key off one stable value ✅.

## 4. Residency (PDPL) & downstream environments
- **Production data stays in-country** on the on-prem Tier-1 footprint (`0C-OPEN-SOURCE-STACK.md`,
  `25-deployment-architecture.md` §1). No production personal data is processed outside the
  residency boundary. 🟡 (verified against target infra at go-live.)
- **Prod data never flows downstream unmasked.** All dry-runs and rehearsals run in **staging with
  masked/synthetic data**. The toolkit enforces this by default: any non-`production` environment is
  flagged `masked=true` and a `production` run requires an explicit `--i-understand-prod` flag plus a
  passed go-live gate ✅ (guardrail in code) / 🟡 (masking pipeline for real extracts is an
  operational step run by the data team).
- **Masking approach:** identifiers are pseudonymized and names/contact fields are synthetic in lower
  environments; the shape/format is preserved so normalization + dedupe are exercised realistically.
  🟡 (data-team runbook; documented here as the required approach).

## 5. Reversibility, idempotency & audit
- **Reversible:** every load is a `migration_batch_id`; `rollback --batch <id>` soft-reverts exactly
  the rows that batch touched and never touches pre-existing rows. Proven on real Postgres ✅.
- **Idempotent:** re-running a batch upserts on natural key + source id — no duplicates. Proven ✅.
- **Audited:** every inserted/updated row emits an audit event (actor=`migration`, purpose,
  provenance) forwarded to audit-service, which applies the WORM **hash chain** on ingest
  (`19-audit-strategy.md`) ✅. The CLI also writes a local JSONL audit artifact per run ✅.

## 6. Dedupe & human oversight
- Deterministic identifier equality auto-merges; a high name score **with an agreeing birth date**
  auto-merges. **Low/medium-confidence pairs are never auto-merged** — they are **held** in a review
  queue for human sign-off before promotion ✅ (unit + integration tested).
- A **dedupe report** (auto-merged / queued-for-review / no-match) is produced each run ✅. Sign-off
  on the review queue is a **hard precondition** for promoting the beneficiary stream 🟡 (human gate).

## 7. Reconciliation
- Each stream emits a **reconciliation report**: source vs loaded (inserted/updated) vs held vs
  rejected, field-mapping coverage, and an exception list with reasons. A migration is **not "done"
  until reconciliation balances and exceptions are triaged** ✅ (balance is asserted; the CLI exits
  non-zero when it doesn't).

## 8. Retention & isolation
- Migrated data inherits the platform's retention + soft-delete (`*_history`) rules; no hard deletes
  (`19-audit-strategy.md`). 🟡 retention windows configured on target infra.
- Post-migration, **provider/tenant isolation** is verified by an automated check before any provider
  user is enabled — no cross-provider leakage (`ProviderIsolationVerifier`, `11-permission-matrix.md`)
  ✅ (logic tested) / 🟡 (run against real RLS in staging as part of cutover).

## 9. Residual risks
| Risk | Mitigation | Status |
|---|---|---|
| Wrong auto-merge collapses two people | never auto-merge below high-confidence + DOB; review queue; reversible by batch | ✅ |
| Unmasked PHI reaches lower env | masked-by-default guardrail; prod flag; DPIA masking approach | ✅ code / 🟡 pipeline |
| Partial/failed load leaves inconsistent state | idempotent re-run + rollback-by-batch; reconciliation must balance before promotion | ✅ |
| Provenance/audit gap | provenance on every row + audit event per mutation | ✅ |

## 10. Sign-off
| Role | Name | Date | Decision |
|---|---|---|---|
| DPO | | | |
| Security owner | | | |
| Operations (data owner) | | | |

> This DPIA is consumed by the **compliance gate** in the release pipeline (phase 12.2); a missing or
> unsigned DPIA **blocks** the staging→prod promotion of any migration.
