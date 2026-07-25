# notification-service

Event-driven **notification** fan-out (Release R5, Phase 8.1 — US-072). Owns the `notification` schema. It is a
fan-out engine, **not** a place business logic lives: it subscribes to domain events, maps each to recipient
role(s) + channel(s), renders a **bilingual (AR/EN)** template with min-necessary **non-clinical** fields, delivers
on live channels, tracks delivery state, and escalates unacted actionable notifications. Notification bodies **never**
carry clinical payload (diagnoses/notes/results).

> Phase 8.1 complete: two live channels (in-app + email) behind an `INotificationChannel` extension point; SMS +
> WhatsApp future-channel **stubs** flagged OFF; idempotent event fan-out; versioned AR/EN templates; time-based
> escalation; delivery tracking with email retry/backoff; a per-user in-app inbox + delivery API; sensitive-context
> send audit.

## Channels (the extension point)

`INotificationChannel { Channel, Enabled, SendAsync }`. A new channel implements it + registers itself; the
dispatcher is channel-agnostic.

- **In-app** (`InAppChannel`) — the persisted notification row **is** the delivery; the inbox reads it. Always on.
- **Email** (`EmailChannel` over `IEmailProvider`) — provider abstraction (dev = `LoggingEmailProvider`; swap
  SMTP/API in Tier 2/3). A provider exception → `Failed`; the delivery-retry sweep re-attempts with capped
  exponential backoff (`NotificationOptions.MaxEmailAttempts`, `RetryBaseSeconds`).
- **SMS / WhatsApp** (`SmsChannel`, `WhatsAppChannel`) — **future-channel STUBS**. They implement the interface so
  the extension point is real, but are disabled by default (`Notification:EnableSms` / `EnableWhatsApp` = false).
  The dispatcher never calls a disabled channel; if asked, the stub logs *"not yet enabled"* and returns `Skipped` —
  **no live send occurs**. To add a real SMS/WhatsApp channel: implement `INotificationChannel`, register it, flip
  the flag.

## Event intake & routing (the fan-out seam)

`POST /api/v1/notifications/ingest` (scope `notification:ingest`) accepts an enriched, routed domain event
(`IngestRequest` → `NotificationEnvelope`): the event id/type, min-necessary non-clinical interpolation `fields`, and
the **role → recipient** resolution (`userId` + `locale`). The routing consumer builds this from the raw domain event
+ the identity/provider directory. **Idempotent**: dedupe on event id (a redelivery is a no-op; a unique
`(event, recipient, channel)` index backstops it under concurrency).

`RoutingTable` (config, not logic) maps each event → template + canonical status text + sensitivity + actionable flag
+ role→channel targets, e.g. approval decision → requesting provider (in-app **+** email) + beneficiary (in-app);
result ready → ordering doctor (in-app); SLA-breaching pending approval → reviewer + Medical Director. Consumed
events include `AuthApproved` / `AuthPartiallyApproved` / `AuthRejected` / `AuthInfoRequested` /
`AuthEmergencyApproved`, `OrderLineAvailable`, `ResultReady`, `RxReady`, `RxLineOutOfStock`, `AppointmentReminder`,
`AppointmentNoShow`.

> **Deferred wiring (fanout bus):** dev uses a per-service in-memory outbox with no fanout exchange, so the live
> broker subscription that turns raw domain events into `ingest` calls lands with the shared event bus (same seam as
> phases 5–7). The dispatcher + seam are fully testable today without it.

## Bilingual templates (AR/EN)

`notification_template` — versioned `{key, locale, subject, body}` rows; **both** `ar` and `en` are authored (Arabic
is RTL, **never** machine-translated at send time). `TemplateRenderer` interpolates only `{token}` placeholders from
the min-necessary field bag; a `ForbiddenKeys` guard (asserted in tests + enforced by the dispatcher) rejects any
clinical key. Status text uses the canonical non-color status vocabulary so in-app items match the design system.

## Escalations

`EscalationService.SweepAsync` — an actionable notification (`AuthInfoRequested`, `AuthSlaBreached`,
`RxLineOutOfStock`) unread past its window escalates to the configured next recipient (supervisor / Medical
Director). The escalation target is resolved + captured at fan-out time, so the sweep needs no directory lookup;
it is **idempotent** (a notification escalates at most once) and escalates once per (event, target) even when several
channel rows exist. Windows are per event type (`RoutingTable.Escalation`).

## Delivery tracking & inbox API (`/api/v1`)

- `GET /notifications` (scope `notification:read`) — the caller's own **in-app inbox** (min-necessary; `unreadOnly`
  filter), newest first. Row-filtered by recipient == caller — never another user's inbox.
- `GET /notifications/{id}/delivery` — per-notification delivery state (`Queued → Sent → Delivered/Failed/Skipped`).
- `POST /notifications/{id}/read` — mark read (acts on the notification → stops its escalation timer).
- `POST /notifications/ingest` — the fan-out seam (above).

Delivery lifecycle per row: `queued → sent → delivered | failed | skipped`; email failures retry with backoff. Sends
of **sensitive-context** notifications write a `Create`/`NOTIFY` audit event via the shared client (bodies carry no
clinical payload; the audit records that a sensitive-context notice was sent, to whom, for which entity).

## Authorization (`libs/authz/NotificationPolicies`, v8.1)

`notification:read` — any authenticated role (empty roles = any), tenant-scoped, handler-row-filtered to the caller's
own notifications. `notification:ingest` — the system fan-out seam. Inbox reads are not flagged sensitive (no clinical
payload); the dispatcher audits sensitive-context sends.

## Domain & data

- `notification` — one row per (event, recipient, channel): channel/locale/status CHECK-constrained; unique
  `(source_event_id, recipient_user_id, channel)` for idempotent fan-out; escalation columns; delivery timestamps.
- `notification_template` — versioned bilingual templates, unique `(template_key, locale, version)`; seeded AR + EN.
- `processed_event` — event-dedupe ledger (fan-out at most once per event id).
- `Infrastructure/Migrations/0001_notification.sql` — schema, tables, indexes, seeded templates, app-role grants.
  Applied to host PG (:55432). The notification store is operational (retention purge allowed), so `DELETE` is
  granted — unlike the append-only audit / decision ledgers.

## Tests

- `TemplateAndRoutingTests` (pure) — AR/EN interpolation, the missing-token + clinical-field guards, the
  event→template/role/channel routing map, and the actionable/escalation config.
- `NotificationAuthzTests` (pure, real engine) — any role reads its own inbox with the read scope; the read scope is
  required; the fan-out seam needs `notification:ingest` and is unreachable with a plain read scope.
- `NotificationDispatchTests` (env-gated `NOTIFICATION_TEST_DB`, live PG, seeded templates) — approval decision fans
  out to the provider on **in-app AND email** in the recipient's locale with **no clinical payload**; a redelivered
  event creates **exactly one** set (idempotent); a failed email **retries with backoff** and the delivery state
  reflects the outcome; an unacted actionable notification **escalates on the timer** (idempotently); a disabled SMS
  channel performs **no live send**; a sensitive-context send is **audited**. Serialized via the `notification-db`
  collection.

Total: 27 notification tests; full solution 456 green.
