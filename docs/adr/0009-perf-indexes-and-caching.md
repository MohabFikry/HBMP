# 9. Performance indexing & caching strategy (perf/DB tuning)

Date: 2026-07-26
Status: Accepted
Phase: 11.1 (Performance, scalability & capacity validation)

## Context

Phase 11.1 profiles the platform under seeded synthetic volume (NFR-012: ≥ 1M beneficiaries,
≥ 10M encounters) and must meet: eligibility p95 ≤ 800 ms (NFR-002), indexed search p95 ≤
700 ms (NFR-004), primary screens p95 ≤ 1.5 s (NFR-001), operational reports p95 ≤ 3 s
(NFR-006), consume p95 ≤ 1 s with zero double-commit (NFR-003/073). Tuning must not change
business behaviour and must keep Row-Level Security predicates index-friendly.

## Decision

Tuning is applied as **two levers only** — covering indexes (schema migrations) and read
caching (Valkey) — each traceable to a measured slow query. No query rewrites that change
results; no denormalisation of clinical data.

### Indexing principles

- **RLS-first composite indexes.** Because every read is filtered by the RLS predicate
  (`tenant_id`, and for provider/branch-scoped tables `provider_id` / `branch_id`), the
  leading column(s) of a covering index must match the RLS predicate so the planner can use
  an index scan under RLS rather than a filtered seq-scan. Pattern:
  `(tenant_id, <status/queue discriminator>, <sort key>) INCLUDE (<projected cols>)`.
- **Worklist / queue indexes.** Approvals worklist (`status='Pending'` ordered by SLA due),
  order provider queue (`status='Ordered'`), pharmacy dispense queue — partial indexes
  `WHERE status = '<open state>'` keep them small and hot.
- **Search indexes.** Beneficiary/order search by business key (`member_no`, `order_ref`)
  uses exact/prefix btree; free-text name search uses `pg_trgm` GIN, both tenant-scoped.
- **TAT projection indexes.** Reporting read-model fact tables index the
  `(dimension, window)` used by the KPI queries so operational reports stay < 3 s.
- Verify each with `EXPLAIN (ANALYZE, BUFFERS)` under RLS `SET ROLE hbmp_app` at volume;
  attach the plan to the migration's ADR note.

### Caching principles (Valkey)

- **Eligibility snapshots** cached with a TTL **and** event-driven invalidation on
  `PolicyChanged` / `CoverageChanged` — a stale eligibility result must never drive a clinical
  decision, so invalidation is mandatory, TTL is only a backstop.
- **Sessions** and **rate-limit counters** cached (already at the gateway/Keycloak layer).
- Measure hit ratio in the perf run; correctness is asserted by an invalidation test (change a
  policy → the next eligibility read reflects it immediately, not after TTL).

## Consequences

- New indexes are additive, backward-compatible migrations (expand/contract); each ships with
  its `EXPLAIN` evidence. Index bloat is monitored via the DB saturation dashboard.
- Caching correctness leans on event delivery; the outbox relay already guarantees at-least-once
  delivery, and consumers are idempotent, so invalidation is safe to replay.
- Concrete indexes/migrations are added per measured slow query from the staging run; this ADR
  records the **strategy** so those additions are principled, not ad-hoc. Each concrete index
  gets a one-line follow-up note here (query, before/after p95, plan) when landed.

## Follow-up log (per concrete index, filled from staging profiling)

| Migration | Table / query | Before p95 | After p95 | Plan |
|---|---|---|---|---|
| _pending staging profiling_ | | | | |
