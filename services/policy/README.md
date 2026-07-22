# policy-service

Phase 1.2. Owns the `policy` schema: policy, benefit_category, coverage, coverage_limit (15-database-erd §5). `consumed_value` is the **authoritative usage accumulator** (source of truth; incremented by consume/dispense sagas later, read-only here except resets).

## Delivered
- Entities + `0001_policy_schema.sql` (FKs, enum CHECKs, `consumed_value>=0`, coverage_limit history trigger, seeded LAB/IMAGING/PHARMACY/CONSULT/REFERRAL categories).
- **Reset math** (`LimitReset`, 8 tests): period-start boundaries (Monthly/Quarterly/Yearly), reset-due detection, apply→consumed=0 + stamp last_reset; **Lifetime/None never reset**; `Remaining = limit − consumed`.
- APIs: `POST /policies`, `POST /policies/{id}/coverages` (+limits), `GET /coverages?beneficiaryId=` (remaining computed), `POST /coverage-limits/reset-run` (job). Emits **PolicyChanged / CoverageChanged / CoverageLimitChanged** via outbox — phase 2 eligibility consumes these to invalidate snapshots.
- Cross-service `beneficiary_id` is a logical value, not a cross-schema FK.

## Tests (8, green offline)
Reset math per period + limit type; remaining calculation.
