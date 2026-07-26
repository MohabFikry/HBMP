# 13. Durable transactional outbox (supersedes the in-memory interim)

Date: 2026-07-26
Status: Accepted
Phase: 16.2 (Audit remediation — C1)

## Context

`AddHbmpEvents(useInMemory: true)` was wired in all 16 DbContext services, so every domain event
AND every audit emit went through a process-local `ConcurrentQueue` (`InMemoryOutbox`). On a crash or
restart the queue — and every un-relayed event — was lost, and the enqueue was never atomic with the
business commit. This is finding **C1** in `docs/AUDIT-2026-07-26.md`: the outbox pattern was present
in shape only. The audit spine (audit-service consumes `audit.events`) inherited the same fragility.

## Decision

Replace the in-memory outbox with an **EF/Postgres-backed durable outbox**, preserving the `IOutbox`
API so no call site changes:

- **`outbox_message` table per service schema** (`event_id` PK, `event_type`, `destination`,
  `payload jsonb`, `correlation_id`, `occurred_at`, `processed_at`, `attempts`, `last_error`), added by
  one shared DDL template (`OutboxSchema.Ddl`) + `modelBuilder.AddOutbox(schema)` mapping. Additive,
  idempotent migration (`9000_outbox.sql`), granted to the `hbmp_app` NOBYPASSRLS role (16.4).
- **`EfOutbox`** writes the row through the caller's DbContext. **`EfOutboxReader`** claims a batch with a
  single `UPDATE … WHERE event_id IN (SELECT … FOR UPDATE SKIP LOCKED LIMIT n) RETURNING *` — so multiple
  relay instances never double-claim, and `attempts` is incremented atomically at claim time. Rows past
  `EventsOptions.MaxAttempts` are quarantined (skipped) so a poison message never blocks the stream.
- **Durable by default.** `AddHbmpEvents` uses the durable path unless `Events:UseInMemoryOutbox=true`
  (set only in `appsettings.Development.json` and tests). Services bind it with
  `AddHbmpDurableOutbox<TContext>()`.

## Transactionality note (honest scope)

The platform's handlers **commit the business change and then enqueue** (frequently inside a service's own
`BeginTransaction`/`Commit`). To honour the audit's "don't change call sites" constraint, `EfOutbox`
persists the outbox row on its own `SaveChanges` immediately after. This is **durable** — the row is in
Postgres, drained at-least-once by the relay, and survives process/broker failure (consumers dedupe on
`event_id`) — which closes C1's data-loss modes (broker down, relay/process restart, lost audit emits).
The residual gap versus perfect atomicity is the sub-millisecond window between the business commit and
the outbox commit; closing it fully requires reordering handlers to enqueue-before-the-final-save, tracked
as a follow-up. This is strictly stronger than the in-memory interim and never weakens a control.

## Consequences

- Events and audit are durable across restart; broker outages no longer drop events (they drain on
  reconnect). Proven by `EfOutboxDurabilityTests` (env-gated on `EVENTS_TEST_DB`, real Postgres): an
  enqueued row is claimed by a **fresh** context, drains on mark-processed, and a poison row is quarantined
  after `MaxAttempts`.
- In-memory remains available for dev/tests behind the config flag; the full suite (726 tests) stays green.
- Running containers must be rebuilt to pick up the durable path (they otherwise keep the prior image).
