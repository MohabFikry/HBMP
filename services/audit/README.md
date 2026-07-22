# audit-service — the audit spine

Phase 0.3. The immutable, append-only, **hash-chained** audit trail every other service depends on (19-audit-strategy.md; 22-data-dictionary §10.4). Its own bounded context, own schema, own identity, WORM store. **`libs/audit-client` is a mandatory dependency of every future service.**

## Guarantees
- **Append-only** — DB role has INSERT + SELECT only; an `UPDATE/DELETE` trigger denies mutation within retention; RLS restricts reads to the audit roles. See `Infrastructure/Migrations/0001_audit_schema.sql`.
- **Hash-chained** — each record carries `prev_hash` + `record_hash = sha256(canonical record)`. Any insert/delete/reorder/edit breaks the chain and is caught. (`libs/audit-client/HashChain`, `AuditCanonicalizer`.)
- **WORM** — a second, independent copy of every record is written to MinIO with object-lock (Compliance mode + retain-until). `MinioWormStore`.
- **Single write path** — events arrive **only** via RabbitMQ (at-least-once, dedupe on event id). No synchronous write endpoint. `RabbitMqAuditConsumer` → `AuditIngestService`.
- **Verifier** — `VerifierBackgroundService` re-computes the chain every 15 min and raises a critical `integrity.mismatch` alert on any break. `AuditVerifier` + `IIntegrityAlerter`.
- **Reads are audited** — the read API (`/api/v1/audit/...`, scope `audit:read`, Security/Compliance/DPO only) emits its own `audit.read` event.

## Projects
`Api/` (host + read API + OTel) · `Domain/` (ingest, verifier, partition, abstractions) · `Infrastructure/` (EF store, WORM, RabbitMQ, alerter, SQL migrator) · `Tests/`.

## libs/audit-client (used by every service)
`IAuditClient.EmitAsync(AuditEventDraft)` stamps id + correlation (W3C traceparent) + timing and writes to the service's transactional outbox (durable, never a silent no-op). `AuditSnapshot.Minimize` redacts PHI/PII/diagnosis/financial values while capturing field-classes. `AuditAuthEventSink` bridges `libs/auth` auth events into the trail (closes the 0.2 stub).

## Tests (43 across auth + audit-client + audit-service, all green offline)
- Hash chain: tamper/delete/reorder/insert all detected (`HashChainTests`).
- Canonicalization determinism + record-hash exclusion + UTC normalization.
- Minimizer redaction; correlation stamping; auth-sink bridge.
- Ingest: chaining + idempotent duplicate handling + WORM write; verifier alerting.

Integration tests (real Postgres/RabbitMQ/MinIO) run once Docker is up — the append-only DB grant, partition creation, and WORM object-lock are validated there.

## Config
`Auth` (Keycloak) · `ConnectionStrings:Audit` (Postgres) · `Worm` (MinIO) · `Messaging` (RabbitMQ). See `Api/appsettings.json`.
