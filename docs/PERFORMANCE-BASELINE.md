# PERFORMANCE-BASELINE.md — Mersal HBMP (Phase 11.1)

Performance & scalability evidence for the release gate (`08-non-functional-requirements.md`
§1 PERF, §2 SCALE; §15 release-gating NFRs). Numbers are produced by running the versioned
`/perf` k6 suite against a **prod-like staging** environment seeded with synthetic,
volume-representative data (NFR-012: ≥ 1M beneficiaries, ≥ 10M encounters). **No PHI** appears
in perf data, logs, or this report (NFR-042).

## How this document is produced (reproducible)

1. Seed synthetic volume: `perf/data-gen/generate.mjs` → load into staging `synthetic` schema
   → project through service ingest APIs (see `perf/data-gen/README.md`).
2. Run the suite through Kong: `perf/run.sh all` (nightly `perf-ci.yml`, or on-demand).
3. Each scenario's k6 `thresholds` encode the NFR target; a miss exits non-zero.
4. Paste the k6 summary numbers into the results table below and link the CI artifact.

> **Status:** harness complete and threshold-gated. The **measured** columns below are
> populated by a staging run against seeded volume; until that run executes they read
> `PENDING (staging)`. This document intentionally does not assert measured numbers that
> were not produced by an actual run — the gate is the run, not this file.

## Targets → scenarios

| # | Scenario | Target (NFR) | k6 script | Measured p95 | Measured p99 | Pass |
|---|---|---|---|---|---|---|
| 1 | Eligibility check | p95 ≤ 800 ms, p99 ≤ 1.5 s (NFR-002) | `01-eligibility.js` | PENDING (staging) | PENDING | — |
| 2 | Reception search | p95 ≤ 700 ms (NFR-004) | `01-eligibility.js` | PENDING | — | — |
| 3 | Order-line CONSUME | p95 ≤ 1 s (NFR-003) | `02-consume.js` | PENDING | — | — |
| 4 | CONSUME no-double-commit | 0 duplicates under parallel consumers (NFR-073) | `02-consume.js` (race) | `double_commit_detected == 0` | — | — |
| 5 | Primary screens (worklists) | p95 ≤ 1.5 s, p99 ≤ 3 s (NFR-001) | `03-worklists.js` | PENDING | PENDING | — |
| 6 | Indexed beneficiary/order search | p95 ≤ 700 ms (NFR-004) | `03-worklists.js` | PENDING | — | — |
| 7 | Operational reports | p95 ≤ 3 s (NFR-006) | `04-dashboards.js` | PENDING | — | — |
| 8 | Event bus sustained | ≥ 200 ev/s buffered, no loss (NFR-014) | `05-mixed-soak.js` | see durability check | — | — |
| 9 | Mixed 1h soak | no latency creep past primary bar | `05-mixed-soak.js` | PENDING | — | — |

## Concurrency (the invariant, not just latency)

`02-consume.js` scenario `consume_race` fires 40 VUs at a **single** order line with distinct
`Idempotency-Key`s (so it tests the real concurrency guard, not idempotency replay). The
`order_fulfillment` ledger is reconciled post-run: **exactly one** committed consumption per
line-generation. `double_commit_detected` must be `0` (threshold `count==0`). This re-proves
the Phase-5 atomic-consume invariant (line `xmin` optimistic concurrency + append-only
ledger + unique idempotency key) holds under sustained parallel load.

## Autoscaling (NFR-010)

Method: run `02-consume.js` / `05-mixed-soak.js` burst profile while watching HPA (CPU/mem on
stateless pods) and KEDA (RabbitMQ/NATS queue depth). Assert pods scale out as depth crosses
the KEDA threshold, the backlog drains, and scale-in follows; throughput scales ~linearly to
≥ 5× baseline. Captured in Grafana (see `infra/observability/grafana/dashboards/`).

| Check | Evidence |
|---|---|
| HPA scales stateless pods under CPU/mem load | PENDING (staging + k3s) |
| KEDA scales workers on queue depth, drains, scales in | PENDING |
| Throughput ~linear to ≥ 5× baseline | PENDING |
| Liveness/readiness + graceful degradation (cached/read-only eligibility) under saturation (NFR-023/024) | PENDING |

## Caching & DB tuning

- **Valkey** caching (eligibility snapshots, sessions, rate-limit counters): measure hit ratio;
  confirm correctness — no stale-eligibility clinical decision, TTL + invalidation on policy
  change. See `docs/adr/0009-perf-indexes-and-caching.md`.
- **Index tuning**: top-N slow queries under load profiled; covering indexes for search,
  worklists, and TAT projections added as migrations, each recorded as an ADR. RLS predicates
  verified to remain index-friendly. See ADR 0009.

## Event-bus durability check (NFR-014)

During `05-mixed-soak.js`, write paths emit domain events via the transactional outbox. After
the run, reconcile: count of state-changes vs. count relayed to RabbitMQ/NATS (stream offsets)
vs. outbox rows marked dispatched — deltas must be 0 (buffered, never lost) at ≥ 200 ev/s.

## Sign-off

| Role | Name | Date | Result |
|---|---|---|---|
| Performance owner | | | Populated at staging run |

_Linked CI artifact: `perf-ci` → `perf-results` (p50/p95/p99, throughput, error rate)._
