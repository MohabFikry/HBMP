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

## Recovery steps
1. Consumer stalled: restart / scale out; KEDA should add workers as depth rises.
2. Poison message: quarantine to DLQ, fix, replay (idempotent → safe).
3. Producer surge: confirm backpressure is buffering (not dropping); let autoscaling drain.

## Verification
- Queue depth draining to baseline; projections/dashboards current; no lost events (outbox
  dispatched-count == relayed-count == consumed-count).

## Post-incident
- If recurring, raise consumer concurrency / KEDA thresholds; add a load test scenario.

## Escalation
- Platform on-call → consumer owner → engineering lead.
