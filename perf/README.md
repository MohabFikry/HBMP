# `/perf` — Mersal HBMP performance, scale & soak harness (Phase 11.1)

Versioned, repeatable load/stress/soak suite that **proves the performance bar** in
`HBMP-Design/08-non-functional-requirements.md` (§1 PERF, §2 SCALE). Runs against a
**prod-like staging** environment through the real ingress + Kong Gateway, exactly as
clients do. **Synthetic/masked data only — never real PHI** (NFR-042).

> This directory is the *harness and methodology*. The signed numbers in
> `docs/PERFORMANCE-BASELINE.md` are produced by executing this suite against staging
> with seeded volume (NFR-012: ≥ 1M beneficiaries, ≥ 10M encounters). The suite fails
> the run (non-zero exit) if any target below is missed, so CI can gate on it.

## Layout

```
perf/
  k6/
    lib/common.js          # shared config, auth (client-credentials), thresholds, helpers
    01-eligibility.js      # NFR-002  eligibility check + reception search bursts
    02-consume.js          # NFR-003/073 order-line CONSUME p95 + parallel no-double-commit
    03-worklists.js        # NFR-001/004 primary-screen search + worklist reads
    04-dashboards.js       # NFR-006  operational reports (async heavy analytics excluded)
    05-mixed-soak.js       # realistic mix, 1h soak; NFR-014 event-bus ≥200 ev/s buffered
  data-gen/
    generate.mjs           # deterministic synthetic volume generator (seedable, masked)
    README.md
  run.sh                   # convenience wrapper: run one script or the whole suite
```

## Targets asserted (run fails if missed)

| Scenario | Target | NFR |
|---|---|---|
| Eligibility API | p95 ≤ 800 ms, p99 ≤ 1.5 s | NFR-002 |
| Order/prescription CONSUME | p95 ≤ 1 s **and** zero double-commit under parallel consumers | NFR-003, NFR-073 |
| Primary screen loads (reception search, worklists) | p95 ≤ 1.5 s, p99 ≤ 3 s | NFR-001 |
| Indexed beneficiary/order search | p95 ≤ 700 ms | NFR-004 |
| Operational reports | p95 ≤ 3 s (heavy analytics async) | NFR-006 |
| Event bus sustained | ≥ 200 events/s buffered, no loss | NFR-014 |

These thresholds are encoded in each k6 script's `thresholds` block, so a missed target
exits non-zero.

## Prerequisites

- [k6](https://k6.io/) ≥ 0.49 (`k6 version`).
- A reachable staging origin (Kong). Default `http://localhost:8000`; override with `BASE_URL`.
- OAuth2 client-credentials for a synthetic load client in Keycloak with the scopes the
  scripts request (`reception:search`, `eligibility:check`, `orders:consume`, …). Set
  `OIDC_TOKEN_URL`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET`. A dev fallback bearer can be
  supplied via `BEARER` for local smoke runs.

## Run

```bash
# whole suite against staging
BASE_URL=https://staging.mersal.internal \
OIDC_TOKEN_URL=https://kc/realms/hbmp/protocol/openid-connect/token \
OIDC_CLIENT_ID=perf-load OIDC_CLIENT_SECRET=*** \
  perf/run.sh all

# a single scenario, small local smoke (few VUs, short)
BASE_URL=http://localhost:8000 BEARER="$TOKEN" SMOKE=1 perf/run.sh k6/01-eligibility.js
```

`SMOKE=1` shrinks VUs/duration for a laptop/CI-smoke pass (proves the script + thresholds
wire up); the full profile is the default and is what the baseline is measured with.

## Seeding synthetic volume

`data-gen/generate.mjs` produces deterministic, masked, volume-representative rows
(seedable via `SEED`) sized to NFR-012. It emits **synthetic** identifiers only — no real
names, no real national IDs, no clinical free-text. See `data-gen/README.md`.

## Guardrails

- No PHI in perf data, logs, or reports (NFR-042). Generated data is synthetic and masked.
- This suite **measures** — it never changes business behaviour. Tuning outputs (indexes,
  caching) land as migrations + ADRs, not as script edits.
