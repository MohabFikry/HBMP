---
name: Mersal Healthcare Platform Architect
description: Positions Mersal as a service-oriented Healthcare Benefit Management Platform (HBMP) and drives microservice boundaries, event-driven choreography, sagas, and open-source on-prem/cloud-ready deployment decisions. Use when making architecture decisions, adding or splitting a service, choosing sync vs async, designing cross-service workflows, or reviewing any design for platform-fit and invariant safety.
---

# Mersal Healthcare Platform Architect

## Purpose
Keep every architectural decision aligned with Mersal being a **Healthcare Benefit Management Platform (HBMP)** — a reusable, service-oriented *benefit administration core* with clinical/EMR and operational domains layered on top — not a single-clinic app. The core (Beneficiaries, Eligibility, Coverage/Policy, Provider Network, Authorizations, Orders, Prescriptions) must stay reusable so Mersal can later add claims, capitation, inventory, PBM, telemedicine, and third-party integrations (UNHCR, government, insurers) without re-platforming.

## When to use / when not to use
- **Use when:** deciding whether new functionality is a new service or belongs in an existing one; defining service boundaries and owned data; choosing synchronous REST vs asynchronous events; designing a cross-domain workflow (approval, consume, activation); reviewing a design/PR for platform-fit; planning open-source on-prem/cloud-ready infra topology; assessing extensibility for future domains.
- **Not for:** field-level schema detail (use Healthcare Database Architect), FHIR/HL7 external contracts (use FHIR Integration Architect), or beneficiary status rules (use Beneficiary Lifecycle Management). Refer out rather than duplicating.

## Mersal domain knowledge & rules
**Service catalog (schema/DB-per-service; no shared tables across boundaries):** api-gateway, identity/auth (`identity`), patient-service (`patient`), policy-service (`policy`), eligibility-service (`eligibility`), provider-service (`provider`), emr-service (`emr`), orders-service (`orders`), approvals-service (`approvals`), pharmacy-service (`pharmacy`), notification-service (`notification`), reporting-service (read models + OpenSearch), audit-service (`audit`), document-service (`document`). Core = identity/patient/policy/eligibility/provider; Clinical/Ops = emr/orders/approvals/pharmacy/notification/reporting/audit/document.
- **Cross-service references are values, not FKs.** Store `beneficiary_id UUID`, never a cross-schema foreign key. Integrity across services is maintained by events + eventual consistency.
- **Sync only when the caller cannot proceed without the result and the op is fast + single-service:** eligibility check (cache-first), order consume, dispense, an update needing immediate confirmation. Everything else (approval routing, notifications, reporting, audit, cache invalidation, cross-service data propagation) is **async events**.
- **Event-driven choreography with transactional outbox.** Every publisher writes the event in the *same transaction* as the state change; a dispatcher relays it (**RabbitMQ** topics/exchanges for ordered domain events; **NATS JetStream / Redpanda** for lightweight fan-out). Envelope = CloudEvents 1.0 with `type` like `hbmp.orders.OrderConsumed.v1`, `subject` = business key (e.g., `ORD-2026-000123`), `correlationid`. Versioning: `.vN` suffix, additive fields, dual-publish on breaking change.
- **Sagas are choreographed, not orchestrated.** Approval saga: `OrderRequested(requiresAuth=true)` → approvals creates AUTHORIZATION → `AuthorizationDecided` → orders advances to Active or Rejected. Compensation on rejection = state rollback + notify; no partial data to undo because consume cannot happen before `Active`. Same pattern governs prescription approval.
- **Order-consume / dispense is the critical invariant** and lives inside one service as a local **serializable** ACID transaction: insert append-only fulfillment/dispense row (UNIQUE idempotency_key) + guarded quantity update + outbox insert, all atomic. Limit decrement in eligibility is eventually consistent via the event; the authoritative usage record is the fulfillment/dispense row, so a lagging cache can never cause double-spend.
- **Idempotency & concurrency:** `Idempotency-Key` header required on unsafe retriable POSTs (consume, dispense, decision, notification send, registration); optimistic concurrency via `row_version` + `If-Match`/ETag (`412` on mismatch); consumers dedupe on `event.id`.
- **BFF pattern:** Web BFF and Mobile BFF aggregate cross-service reads (e.g., beneficiary "360"); they never own domain data. **Kong (OSS)** is the single ingress (JWT/OIDC validation against Keycloak, per-provider/tenant rate limits, correlation IDs, PHI redaction on errors), fronted by Traefik/NGINX Ingress + ModSecurity (OWASP CRS) + Let's Encrypt TLS. Service-to-service stays in-cluster via **Linkerd** mTLS, never the public gateway.
- **Provider/tenant isolation:** single logical tenant (Mersal) with provider-scoped isolation as the primary boundary, enforced in three layers — **Keycloak** `provider_id` claim, Kong scope + context header, PostgreSQL RLS. Beneficiary clinical data is NOT provider-partitioned (a refugee may be seen by many providers); access governed by role + care-relationship and audited on every read.
- **Open-source, on-prem-first, cloud-ready ($0 licensing — Mersal is a charity):** **k3s** (on-prem, HPA on CPU + queue depth) + **Docker Compose** (single-node Tier 1) + **Helm**, same charts lift-and-shift to cloud (Tier 3); **Kong** gateway; **RabbitMQ** + **NATS JetStream**; **PostgreSQL** (schema-per-service, LUKS + pgcrypto, **Patroni** HA, **pgBackRest** PITR, creds in **OpenBao**); **Valkey** cache; **OpenSearch**; **MinIO** (S3, SSE, object-lock/WORM); **OpenBao/Vault** + SOPS secrets/KMS; **Keycloak** IdP; **Linkerd** mesh/mTLS; **OpenTelemetry + Prometheus + Grafana + Loki + Tempo** (LGTM) observability. Backend .NET 8 and all application logic unchanged — only infra product names differ (see `../../0C-OPEN-SOURCE-STACK.md`).

## Key entities, states & invariants
- Canonical business keys: `MRS-M-*` (member), `ENC-*`, `ORD-*`, `RX-*`, `AUTH-*`, `REF-*`. Surrogate PKs are UUID v7.
- Canonical lifecycles come from `../../23-state-machines.md`; every transition writes an append-only audit event; illegal transitions are rejected and audited as `TransitionDenied`.
- Invariants that architecture must never break: atomic + idempotent + duplicate-proof consume/dispense; no-reuse of consumed lines; minimum-necessary field access; immutable hash-chained audit; soft-delete + `_history`; provider/tenant isolation.
- Resilience patterns are mandatory on the relevant seams: transactional outbox, retry w/ backoff + jitter, dead-letter queues, circuit breakers on sync s2s calls, timeouts everywhere, idempotent consumers, health/readiness probes, graceful degradation (eligibility falls back to conservative `NeedsAuthorization` if cache + policy are down).

## How to apply
- When adding capability, first ask: *does this belong to an existing bounded context, or is it a new reusable core service?* Prefer extending the correct owner over creating cross-schema coupling.
- For every write that touches benefit consumption, insist on the append-only + idempotency-key + guarded-update + outbox pattern; reject designs that mutate a running total without a serializable guard.
- Default new cross-domain interactions to **events**; justify any new synchronous call against the "caller cannot proceed" test.
- In reviews, flag: cross-service FKs, shared tables, consume/dispense outside a local transaction, missing outbox, missing idempotency key, PHI leaking into search/logs, provider isolation bypass, orchestration where choreography suffices.
- Preserve extensibility: keep the benefit core (eligibility/coverage/authorization) decoupled from clinical specifics so claims/PBM/telemedicine/UNHCR adapters attach at the edges (see FHIR Integration Architect).

## Canonical references
- Service catalog, C4, events, sagas, isolation, resilience: `../../16-service-architecture.md`
- Foundations & HBMP framing: `../../0A-DESIGN-FOUNDATIONS.md`
- Open-source on-prem/cloud-ready topology, networking, HA/DR: `../../25-deployment-architecture.md`
- Free/open-source stack decision & Azure→OSS mapping (deployment tiers): `../../0C-OPEN-SOURCE-STACK.md`
- Lifecycles referenced by sagas: `../../23-state-machines.md`; keys/enums: `../../22-data-dictionary.md` §11
- Data model & schema ownership: `../../15-database-erd.md`; RLS/PHI: `../../18-security-model.md`

## Guardrails
- Never introduce a foreign key or a shared table across service boundaries; cross-service links are identifiers maintained by events.
- Never make consume/dispense a distributed transaction; it is one local serializable transaction with outbox relay.
- Never use synchronous calls for approval routing, notifications, reporting, audit, or cache invalidation.
- Never partition beneficiary clinical data by provider; enforce access by role + care-relationship + audit.
- Do not invent services, events, or keys outside those defined in the referenced design docs; extend the canonical catalog, don't fork it.
