# Runbook: event-bus backlog

- **Trigger:** `EventBusBacklogGrowing` (RabbitMQ ready messages > 5k) or KEDA not scaling consumers.
- **Impact:** delayed projections (reporting read-model, notifications), stale dashboards, approvals worklist lag.
- **Owner / on-call:** platform on-call + affected consumer owner.

## Background
Publishing uses a **transactional outbox** (event emitted in the same txn as the state change);
a relay moves it to RabbitMQ (queues) / NATS JetStream (stream). Consumers are **idempotent**
(dedupe on event id), so re-delivery is safe. NFR-014: ≥ 200 ev/s buffered without loss.

## Diagnosis checklist
1. Which queue is backed up (RabbitMQ mgmt / metrics)? Producer surge or consumer stall?
2. Is KEDA scaling consumers on queue depth? Check HPA/KEDA + pod health.
3. Consumer crash-looping (poison message)? Check logs (Loki) for the failing event id.

## The dead-letter queue
Consumers reject a message they cannot process with `requeue:false`. Until 2026-08-09 that **dropped it**:
no queue declared `x-dead-letter-exchange` and no dead-letter exchange existed, so the step below — which
this runbook has always instructed — had nowhere to quarantine anything to.

Every queue except the dead-letter queue itself now carries the `hbmp-dead-letter` policy, which routes
rejections to the `hbmp.dlx` fanout exchange and from there to **`hbmp.dead-letter`**. It is applied by the
one-shot `rabbitmq-init` compose service, as a policy rather than as queue arguments because arguments
cannot be changed after a queue is declared. `DeadLetterQueueNotEmpty` alerts on any depth at all: the
queue's normal state is empty, and a message sitting in it is a domain event that was never applied.

## Recovery steps
1. Consumer stalled: restart / scale out; KEDA should add workers as depth rises.
2. Poison message: it is already parked in `hbmp.dead-letter`. Read it there, fix the cause, then replay
   it onto its original queue (`x-death` on the message names it). Consumers dedupe on event id, so a
   replay of something that did partially apply is safe — see `failed-consume-replay.md`.
3. Producer surge: confirm backpressure is buffering (not dropping); let autoscaling drain.

## Verification
- Queue depth draining to baseline; projections/dashboards current; no lost events (outbox
  dispatched-count == relayed-count == consumed-count).

## Post-incident
- If recurring, raise consumer concurrency / KEDA thresholds; add a load test scenario.

## Escalation
- Platform on-call → consumer owner → engineering lead.
