# Runbook: failed-consume replay

- **Trigger:** `FailedConsumeSpike` alert (`orders_consume_failed_total` increasing).
- **Impact:** lab/imaging results not attaching to order lines → clinicians missing results.
- **Owner / on-call:** orders/fulfillment service owner.

## Background (invariant to protect)
Consume is **atomic + idempotent**: `order_fulfillment` is append-only, keyed by a unique
idempotency key, with line `xmin` optimistic concurrency. **Replays are safe** — a duplicate
consume with the same idempotency key is a no-op, and the concurrency guard prevents
double-commit. This runbook never disables that guard.

## Diagnosis checklist
1. Inspect failed events (DLQ / failed_total by reason): validation? auth gate? transient DB?
2. Distinguish transient (retry) from poison (bad payload — quarantine, don't hot-loop).

## Recovery steps
1. Transient: re-drive the DLQ / re-publish the event; idempotency makes this safe.
2. Poison: quarantine the message, open a data ticket, fix source, then replay.
3. Verify no double-commit occurred: `order_fulfillment` has exactly one committed row per line-generation.

## Verification
- `orders_consume_failed_total` flat; backlog drained; ledger shows no duplicates.

## Post-incident
- If a class of payloads fails, add validation upstream + a contract test.

## Escalation
- Service owner → integration lead. Suspected data corruption → platform lead + DPO.
