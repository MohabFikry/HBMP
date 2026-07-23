# eligibility-service

Computes benefit **eligibility decisions** and serves a cache-first, event-invalidated read model.
Phase 2.1 (Release R1). Owns the `eligibility` schema.

## Responsibility

`POST /api/v1/eligibility/check` returns a decision from **exactly** `{Eligible, Ineligible, NeedsAuthorization}`,
computed from three inputs (17-api-specifications §5, 23-state-machines §1):

1. **Member status** — only `Active` can be Eligible; `Suspended/Expired/Blocked/Inactive/Pending` → `Ineligible`.
2. **Coverage validity** — an active coverage for the requested benefit category, in effect on the check date.
3. **Remaining limits** — `remaining = limit_value − consumed_value`. An exhausted limit or a gated/pre-auth
   service → `NeedsAuthorization` (a soft No routed to approvals, not a denial).

The decision logic lives in the pure, unit-tested `EligibilityEngine` (Domain). Snapshots and the Valkey
cache are **derived** read models — never a source of truth; policy-service `coverage_limit.consumed_value`
remains authoritative.

## Request / response

```
POST /api/v1/eligibility/check      scope: eligibility:check
{ "beneficiaryId": "…", "benefitCategory": "CONSULT", "serviceCode": "C001", "serviceRequiresPreAuth": false }
→ 200 { decision, coverageId, reasons[], limitState{ limitType, limitValue, consumedValue, remaining },
        snapshotExpiresAt, fromCache }
```

Every check is an audited **PHI read** (`libs/audit-client`).

## Cache + event-driven invalidation

- Cache-first in **Valkey** (`Cache:Valkey`), keyed by `(beneficiaryId, benefitCategory)`, TTL 15 min.
  Falls back to an in-memory cache when Valkey is not configured (tests / single-node dev).
- A background `EventConsumer` consumes `patient.events` + `policy.events` (at-least-once, idempotent on
  event id) and updates the local read models, **invalidating** the cache on `BeneficiaryActivated`,
  `BeneficiaryStatusChanged`, `CoverageChanged`, `CoverageLimitChanged`, and `PolicyChanged`.

## Data

Migration `Infrastructure/Migrations/0001_eligibility.sql` — `member_projection`, `coverage_projection`,
`eligibility_snapshot`, `processed_event`. Apply with `psql` against the service database.

## Reception search (2.2, US-010)

```
GET /api/v1/reception/search?q=<NationalID | Passport | Card | Policy | Phone | name>
     scope: reception:search
→ 200 { query, count, results[ ReceptionResultCard ], emptyStateHint? }
```

`ReceptionResultCard` is a **minimum-necessary** projection (11-permission-matrix): identity (memberNo,
display name, status + **non-color status semantics**), active coverage categories, remaining limits, and a
**visit-history summary** (count + last-visit date/type only). It carries **no** diagnoses, notes, orders,
prescriptions, results, or vitals — the type cannot represent EMR data, so it cannot leak via query
manipulation. Projection happens server-side. Every search is an audited PHI read.

**Search backend.** `IReceptionIndex` is backed by `PostgresReceptionIndex` (default) which reads the
min-necessary projections directly (always in sync, well under the 2 s p95 target via identifier indexes).
A dedicated search cluster (OpenSearch) is a drop-in behind the same interface — the min-necessary boundary
and the endpoint are unchanged. The projections deliberately contain **only** min-necessary columns, so no
index configuration can widen what reception sees.

## Tests

- `EligibilityEngineTests` — the decision matrix across the three inputs (Eligible / Ineligible per bad
  status / no-or-expired coverage / NeedsAuthorization for gated + exhausted / binding-limit selection).
- `EligibilityCacheTests` — cache hit / miss / TTL expiry / scoped invalidation.
- `ReceptionSearchTests` — lookup by each identifier type + empty state + card composition.
- `ReceptionMinNecessaryTests` — **authorization test** proving the reception card/document carry no
  EMR/clinical field (reflection over the full type graph against an EMR-term denylist).
