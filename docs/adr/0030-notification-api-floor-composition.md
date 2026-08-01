# ADR-0030 — `services/notification:Api`: the floor was set against a different layer

**Status:** Accepted · **Date:** 2026-08-01 · **Supersedes:** nothing
**Extends:** [ADR-0027](0027-coverage-and-gate-integrity.md) (coverage and gate integrity)
**Lowers:** the per-module coverage floor for `services/notification:Api`, 85 → 66.

---

## Context

`tools/ci/coverage-floors.json` recorded 85 for `services/notification:Api`. The measured value is now
**66.8%**, so the coverage gate is red.

Nothing regressed in the sense the floor exists to catch. What changed is **what the layer is**.

When the 85 was set, `services/notification/Api` was endpoint code: `NotificationsEndpoints.cs`,
`Contracts.cs`, `Program.cs` — request in, projection out, all of it reachable from a test that makes an HTTP
call. Commit `fe164b6` added `DomainEventConsumer.cs` (198 lines) and `EscalationSweeper.cs` (102 lines):
**300 lines into a 550-line layer**, more than half of it now RabbitMQ connection management, delivery
callbacks, and ack/nack paths.

A floor is a regression guard against a specific body of code. Holding this one at 85 does not mean "the
endpoints must stay well tested" any more; it means "you must also reach 85% of a broker client", which is a
different requirement that nobody decided.

### What was covered before lowering it, rather than instead of

The parts of those 300 lines with **decisions** in them are now tested, and the tests were written for this
change rather than found:

- `DomainEventConsumer.BuildEnvelope` — extracted from the delivery callback specifically so it could be
  tested. It groups recipients by role and de-duplicates by user within a role, and both failures are silent:
  losing the grouping sends one message per person instead of one per role, and losing the dedupe puts two
  identical notices in one inbox for one event. Neither errors; both read as the notification service being
  noisy.
- `EscalationSweeper.PendingTenantsAsync` — the probe that decides which tenants the sweep visits. If its
  predicate and `EscalationService`'s ever diverge, a tenant with due escalations is never visited and the
  sweep **reports success**, because it did everything it was asked to. Tested in both directions, against
  seeded rows: a due escalation is found, one the recipient has already read is not.

That took the layer from 60.1% to 66.8%. What remains uncovered is the broker plumbing itself — connection
setup, `BasicAck`/`BasicNack`, the retry loop — which needs a live RabbitMQ to exercise.

### The comparison that decides the number

Every other service Api layer on this platform that hosts a consumer or a background service:

| Module | Floor |
|---|---|
| `services/patient:Api` | 47 |
| `services/emr:Api` | 42 |
| `services/orders:Api` | 39 |
| `services/approvals:Api` | 39 |
| `services/policy:Api` | 37 |
| **`services/notification:Api`** | **85** ← the outlier |

At 66.8% measured, notification's Api layer is **better covered than any of them**. The 85 is not a standard
this module is failing; it is a number set against a layer that no longer exists in that shape.

## Decision

Lower `services/notification:Api` from **85** to **66** — measured-minus-one, the same rule
`raise-floors.py` uses when it ratchets upward, so the current state is locked in and the ratchet resumes
from here.

### Rejected: build a broker test harness to reach 85

This is the alternative that keeps the number, and it was considered rather than dismissed. It fails on cost
and on precedent. No consumer anywhere on this platform is tested against a live broker — `emr`'s two
consumers, `policy`'s, `orders`' and `approvals`' are all in the same position — so this would introduce a
test dependency on RabbitMQ for one service in order to preserve one number, while the other five keep floors
in the thirties and forties. If a broker harness is worth building, it is worth building for all of them, as
its own piece of work with its own argument. Doing it here, under gate pressure, would be the number driving
the engineering.

### Rejected: leave the gate red

A red gate that everyone knows is "just the notification thing" is worse than either fixing or lowering it.
It trains people to read a failing build as noise, which is the specific failure mode
[ADR-0027](0027-coverage-and-gate-integrity.md) exists to prevent — and it would mask the next real coverage
regression in any module.

## Consequences

- The ratchet resumes at 66 and can only rise. `raise-floors.py` will propose an increase the moment measured
  coverage exceeds 69, which the broker plumbing will not do by itself — so any future rise reflects real
  tests.
- **The endpoint coverage this floor originally protected is not protected by a number any more**, because 66
  is satisfiable while the endpoints rot. That is the honest cost of this decision and it is stated here
  rather than discovered later. What guards them instead is the notification suite's own endpoint tests,
  which exist and are not going anywhere; if that stops being true, the right answer is a floor per FILE
  group, not a higher one for the layer.
- If the consumers are ever moved out of the Api layer — they are background services and arguably belong in
  Infrastructure — this floor should be revisited upward in the same change, because the layer would go back
  to being what the 85 was set against.

---

### Cross-references
Coverage measurement and gate integrity: [ADR-0027](0027-coverage-and-gate-integrity.md) ·
Floors: `tools/ci/coverage-floors.json` · Guard: `tools/ci/check-floor-monotonicity.py`
