# 16 — Service Architecture (Microservices)

> Part of the **Mersal Healthcare Benefit Management Platform (HBMP)** design workspace.
> Up: [00-README-INDEX.md](00-README-INDEX.md) · Foundations: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [15-database-erd.md](15-database-erd.md) · [17-api-specifications.md](17-api-specifications.md) · [23-state-machines.md](23-state-machines.md) · [18-security-model.md](18-security-model.md) · [25-deployment-architecture.md](25-deployment-architecture.md)
> **Infrastructure note:** the infra names below (gateway, event bus, storage, search, cache, secrets, identity, observability, orchestration) follow the open-source stack in [0C-OPEN-SOURCE-STACK.md](0C-OPEN-SOURCE-STACK.md), which is authoritative. **Service boundaries, the event catalog, sagas, and invariants are unchanged.**

---

## 1. Architectural Goals

HBMP is a **microservices** platform of reusable core services (identity, patient, policy, eligibility) plus EMR/clinical and operational services. Design goals:

- **Domain isolation** — schema/DB per service; no shared tables across service boundaries.
- **Strong invariants where it matters** — order-line consume and dispense are atomic, idempotent, and impossible to double-count (see [23-state-machines.md](23-state-machines.md)).
- **Loose coupling elsewhere** — event-driven choreography for cross-domain workflows, with sagas for multi-step consistency.
- **Auditability & compliance** — every mutation produces an audit event; PHI/PII handled per [18-security-model.md](18-security-model.md).
- **Open-source, on-prem-first operations** — k3s (Docker Compose at Tier 1), Kong Gateway, RabbitMQ + NATS JetStream, self-hosted PostgreSQL (Patroni HA), Valkey, OpenSearch, MinIO, OpenBao ([0C-OPEN-SOURCE-STACK.md](0C-OPEN-SOURCE-STACK.md)).

---

## 2. Service Catalog

| Service | Responsibility | Owned data (schema) | Key APIs | Publishes | Consumes |
|---|---|---|---|---|---|
| **api-gateway** | Edge routing, authN validation, rate limit, request aggregation entry | none (config) | all `/api/v1/*` proxied | — | — |
| **identity/auth** | Keycloak (OIDC) integration, RBAC, token/scope issuance | `identity` | `/users`, `/roles`, token introspection | `UserProvisioned`, `RoleAssigned` | `ProviderOnboarded` |
| **patient-service** | Beneficiary master, identifiers, contacts, family | `patient` | `/beneficiaries`, `/beneficiaries/{id}/identifiers` | `BeneficiaryRegistered`, `BeneficiaryStatusChanged` | `DocumentAttached` |
| **policy-service** | Policies, coverage, limits, benefit categories | `policy` | `/policies`, `/coverages` | `CoverageActivated`, `CoverageLimitChanged` | `BeneficiaryStatusChanged` |
| **eligibility-service** | Compute + cache eligibility snapshots | `eligibility` | `/eligibility/check` | `EligibilityRecomputed` | `CoverageActivated`, `CoverageLimitChanged`, `OrderLinesConsumed`, `RxLinesDispensed` |
| **emr-service** | Appointments, encounters, SOAP notes, diagnoses, vitals, allergies, med history | `emr` | `/appointments`, `/encounters`, `/encounters/{id}/notes` | `EncounterCreated`, `EncounterFinished`, `DiagnosisRecorded` | `BeneficiaryRegistered` |
| **orders-service** | Investigation orders, lines, fulfillment (consume) | `orders` | `/investigation-orders`, `.../consume` | `OrderRequested`, `OrderApproved`, `OrderLinesConsumed`, `OrderCompleted` | `AuthorizationDecided`, `EncounterCreated` |
| **approvals-service** | Authorizations & decisions, referrals | `approvals` | `/authorizations`, `.../decision`, `/referrals` | `AuthorizationRequested`, `AuthorizationDecided`, `ReferralCreated` | `OrderRequested`, `PrescriptionSubmitted` |
| **pharmacy-service** | Prescriptions, lines, dispense events | `pharmacy` | `/prescriptions`, `.../dispense` | `PrescriptionSubmitted`, `PrescriptionApproved`, `RxLinesDispensed` | `AuthorizationDecided` |
| **provider-service** | Providers, locations, contracts, service lines | `provider` | `/providers`, `/providers/{id}/contracts` | `ProviderOnboarded`, `ContractActivated` | — |
| **claims-service** *(Phase 10b)* | Claim origination (auto-derived, provider-submitted, beneficiary reimbursement), batching, pre-adjudication, line-level decisions, adjustments, settlement advice ([36-claims-management.md](36-claims-management.md)) | `claims` | `/claims`, `/claims/{id}/lines/{lineId}/decision`, `/claim-batches`, `/claim-batches/{id}/settlement-advice`, `/reimbursement-requests` | `ClaimCreated`, `ClaimSubmitted`, `ClaimAdjudicated`, `ClaimLineDecided`, `ClaimApproved`, `ClaimPartiallyApproved`, `ClaimDenied`, `ClaimAdjusted`, `ClaimVoided`, `ClaimAppealed`, `ReimbursementSubmitted`, `ReimbursementMatched`, `ReimbursementRequiresManualAssessment`, `BatchCreated`, `BatchUnderReview`, `BatchDecided`, `SettlementAdviceIssued` | `OrderLinesConsumed`, `RxLinesDispensed`, `AuthorizationDecided`, `CoverageChanged` |
| **notification-service** | Templated multi-channel notifications | `notification` | `/notifications` (internal) | `NotificationSent` | *(most domain events)* |
| **reporting-service** | Read models, dashboards, exports | read replicas + OpenSearch | `/reports/*` | — | *(all domain events)* |
| **audit-service** | Central append-only audit log | `audit` | `/audit/events` (query) | — | *(audit stream from all)* |
| **document-service** | Document metadata + MinIO object-storage orchestration | `document` | `/documents`, `/documents/{id}/content` | `DocumentAttached` | — |

Supporting infra: **Event Bus** (NATS JetStream domain events), **Object Storage** (MinIO, S3-compatible, WORM), **Relational DB** (self-hosted PostgreSQL, Patroni HA), **Search Engine** (OpenSearch), **Caching** (Valkey), **Message Queue** (RabbitMQ durable/quorum queues for commands/outbox). Secrets/keys in **OpenBao**; orchestration on **k3s** (Docker Compose at Tier 1).

### 2.1 claims-service boundaries & invariants (Phase 10b)

`claims-service` is a **financial-adjudication** bounded context added on top of the completed core (authorizations, fulfillment, contracts/tariffs) — see [36-claims-management.md](36-claims-management.md) for the authoritative design.

- **Owned data (`claims` schema):** `claim`, `claim_line`, `claim_decision`, `claim_adjustment`, `claim_batch`, `reimbursement_request`, `claim_document`, `ocr_extraction`. Decisions and adjustments are **append-only**; corrections are compensating adjustments or Void + re-claim, never edits or hard deletes.
- **Synchronous dependencies (in-mesh, never via the public gateway):** provider-service (contract tariffs + network/contract effectivity), policy-service/eligibility-service (coverage validity on the service date), approvals-service (authorization linkage and scope caps), document-service (invoices/receipts/results, OCR, WORM-stored settlement advice), masterdata (CPT/LOINC/LOCAL/drug code validation).
- **Never reads emr-service.** Claims adjudicate on codes and amounts. Diagnoses, notes and lab/imaging **result values** are outside this service's blast radius; result *existence* (date + document reference) is the only evidence surfaced. Lines needing medical-necessity judgement are routed to a clinical reviewer in approvals-service, who owns that view.
- **Never executes payments.** The terminal artifact is an immutable settlement advice handed to Finance/treasury; money moves outside the platform. An optional external payment reference may be recorded back against the batch.
- **Never re-decrements coverage.** `order_fulfillment`/`dispense_event` rows remain the authoritative usage record; claims reconcile against them and never maintain a parallel accumulator.
- **Cross-service references are values, not FKs** — `beneficiary_id`, `provider_id`, `authorization_id`, `order_fulfillment_id`, `dispense_event_id` are stored as identifiers with denormalized display fields, consistent with schema-per-service isolation (§1).
- **Outbox + idempotency** as everywhere else: every claims event is written in the same transaction as the state change and relayed; consumers dedupe on `event.id`; claim submission, decision, adjustment and batch settlement require an `Idempotency-Key` ([17-api-specifications.md](17-api-specifications.md)). `UNIQUE` on the fulfillment/dispense reference enforces one payable line per delivered item (`DUPLICATE_CLAIM`), and a partial unique index keeps a claim in at most one open batch.

> **Naming note:** `OrderLinesConsumed` and `RxLinesDispensed` are the names the **implemented** orders/pharmacy services actually emit (phases 5 & 6) — this catalog and [36](36-claims-management.md) match the shipped code. `CoverageChanged` in [36](36-claims-management.md) corresponds to this catalog's `CoverageActivated` + `CoverageLimitChanged` pair.

---

## 3. C4 — System Context

```mermaid
C4Context
    title HBMP System Context
    Person(caseworker, "Case Worker", "Registers beneficiaries, checks eligibility")
    Person(clinician, "Clinician", "Documents encounters, orders labs, prescribes")
    Person(pharmacist, "Pharmacist", "Dispenses medication")
    Person(approver, "Benefit Approver", "Authorizes orders/prescriptions/referrals")
    Person(provider_user, "Provider Staff", "Performs labs/imaging, fulfills orders")
    Person(claims_officer, "Claims Officer", "Reviews claim batches, decides lines, issues settlement advice")

    System(hbmp, "HBMP Platform", "Benefit administration + EMR for refugee beneficiaries")

    System_Ext(entra, "Keycloak", "Identity provider - OIDC/OAuth2 + MFA")
    System_Ext(sms, "SMS/Email Gateway", "Notification delivery")
    System_Ext(unhcr, "External Registries", "UNHCR / refugee identity references")

    Rel(caseworker, hbmp, "Uses", "HTTPS")
    Rel(clinician, hbmp, "Uses", "HTTPS")
    Rel(pharmacist, hbmp, "Uses", "HTTPS")
    Rel(approver, hbmp, "Uses", "HTTPS")
    Rel(provider_user, hbmp, "Uses", "HTTPS")
    Rel(claims_officer, hbmp, "Uses", "HTTPS")
    Rel(hbmp, entra, "AuthN/OIDC")
    Rel(hbmp, sms, "Sends notifications")
    Rel(hbmp, unhcr, "Validates identifiers (batch)")
```

---

## 4. C4 — Container Diagram

```mermaid
flowchart TB
    subgraph Edge
        FD[Ingress + ModSecurity WAF - OWASP CRS]
        APIM[Kong Gateway - OSS]
    end
    subgraph BFF
        WEBBFF[Web BFF]
        MOBBFF[Mobile BFF]
    end
    subgraph Core[Core Services - k3s]
        IDS[identity/auth]
        PAT[patient-service]
        POL[policy-service]
        ELG[eligibility-service]
        PRV[provider-service]
    end
    subgraph Clinical[Clinical/Ops Services - k3s]
        EMR[emr-service]
        ORD[orders-service]
        APR[approvals-service]
        PHA[pharmacy-service]
        CLM[claims-service]
        NOT[notification-service]
        RPT[reporting-service]
        AUD[audit-service]
        DOC[document-service]
    end
    subgraph Infra[Shared Infrastructure]
        SB[(RabbitMQ + NATS JetStream)]
        PG[(PostgreSQL + Patroni HA\nschema-per-service)]
        RED[(Valkey Cache)]
        SRCH[(OpenSearch)]
        BLOB[(MinIO - S3, SSE + WORM)]
        KV[(OpenBao)]
    end

    FD --> APIM --> WEBBFF & MOBBFF
    WEBBFF --> IDS & PAT & POL & ELG & PRV & EMR & ORD & APR & PHA & CLM & DOC & RPT
    MOBBFF --> PAT & ELG & EMR & ORD & PHA & NOT

    Core --- PG
    Clinical --- PG
    ELG --- RED
    PAT --- RED
    RPT --- SRCH
    DOC --- BLOB
    CLM -- tariffs --> PRV
    CLM -- auth linkage --> APR
    CLM -- docs + OCR + WORM advice --> DOC
    Core -. events .-> SB
    Clinical -. events .-> SB
    SB -. fan-out .-> NOT & RPT & AUD & ELG
    Clinical --- KV
    Core --- KV
```

---

## 5. API Gateway & BFF Pattern

- **Kong Gateway (OSS)** is the single ingress (behind Traefik/NGINX Ingress + ModSecurity WAF). It validates JWTs from Keycloak, enforces **rate limits/quotas per consumer (provider/tenant)**, injects correlation IDs, and applies request/response policies (header stripping, PHI redaction on error paths).
- **BFF layer** — two backends-for-frontends (Web BFF for the staff portal, Mobile BFF for field case workers). BFFs **aggregate** cross-service reads (e.g., a beneficiary "360" view combining patient + coverage + eligibility + recent encounters) so clients make one call. BFFs never own domain data; they orchestrate and shape responses.
- Downstream service-to-service calls stay **inside the cluster mesh** (Linkerd mTLS), never via the public gateway.

---

## 6. Sync vs Async Boundaries

| Interaction | Style | Rationale |
|---|---|---|
| Client → service reads/commands | **Sync REST** via Kong/BFF | Immediate UX feedback |
| Eligibility check | **Sync** (cache-first) | Blocking decision at point of care |
| Order consume / dispense | **Sync** transactional command | Must return authoritative success/failure |
| Approval routing, notifications, reporting, audit, cache invalidation | **Async events** | Decoupled, resilient, non-blocking |
| Cross-service data propagation (e.g., beneficiary → EMR) | **Async events** (eventual consistency) | Avoid distributed FK/temporal coupling |

**Rule of thumb:** synchronous only when the caller *cannot proceed* without the result and the operation is fast + owned by one service. Everything else is an event.

---

## 7. Event Bus & Event Catalog

Transport: **NATS JetStream** (durable streams, at-least-once, ordered subjects where ordering matters) for domain events; **RabbitMQ** (durable/quorum queues, DLQ) for commands and outbox relay. Every publisher uses the **transactional outbox** pattern. (Redpanda/Kafka-API is a drop-in for higher-volume streaming.)

### 7.1 Event envelope (CloudEvents 1.0)

```json
{
  "specversion": "1.0",
  "type": "hbmp.orders.OrderLinesConsumed.v1",
  "source": "orders-service",
  "id": "01J...", 
  "time": "2026-07-21T10:15:00Z",
  "subject": "ORD-2026-000123",
  "correlationid": "corr-abc",
  "datacontenttype": "application/json",
  "data": { "orderId": "...", "orderLineId": "...", "quantity": 1, "beneficiaryId": "..." }
}
```

### 7.2 Event catalog (selected)

| Event `type` | Producer | Key consumers | Purpose |
|---|---|---|---|
| `hbmp.patient.BeneficiaryRegistered.v1` | patient | emr, policy, reporting | Seed downstream read refs |
| `hbmp.patient.BeneficiaryStatusChanged.v1` | patient | policy, eligibility | Suspend/expire cascades |
| `hbmp.policy.CoverageActivated.v1` | policy | eligibility | Recompute snapshot |
| `hbmp.policy.CoverageLimitChanged.v1` | policy | eligibility | Invalidate cache |
| `hbmp.orders.OrderRequested.v1` | orders | approvals | Trigger authorization if required |
| `hbmp.approvals.AuthorizationDecided.v1` | approvals | orders, pharmacy | Advance order/rx state |
| `hbmp.orders.OrderLinesConsumed.v1` | orders | eligibility, reporting, audit | Decrement limits, analytics |
| `hbmp.pharmacy.RxLinesDispensed.v1` | pharmacy | eligibility, reporting, audit | Decrement pharmacy limits |
| `hbmp.document.DocumentAttached.v1` | document | orders, emr | Link results to orders/encounters |
| `hbmp.claims.ClaimLineDecided.v1` | claims | reporting, notification, audit | Roll decisions up to batch totals, notify payee |
| `hbmp.claims.BatchDecided.v1` | claims | claims (settlement), reporting | Freeze rollups, trigger settlement advice |
| `hbmp.claims.SettlementAdviceIssued.v1` | claims | notification, reporting, audit | Hand-off artifact to Finance/provider (no payment execution) |
| `hbmp.*.` (all) | all | audit, reporting | Audit + read-model projection |

Versioning: event `type` carries `.vN`; new fields are additive; breaking changes bump the version and run **dual-publish** during migration.

---

## 8. Consistency & Sagas

### 8.1 Order-consume — the critical invariant

Consume is a **local ACID transaction** in orders-service (no distributed transaction needed for the write itself):

```mermaid
sequenceDiagram
    participant P as Provider (client)
    participant ORD as orders-service
    participant DB as orders schema (PG)
    participant SB as RabbitMQ/NATS (outbox)
    participant ELG as eligibility-service

    P->>ORD: POST /investigation-orders/{id}/consume (Idempotency-Key)
    ORD->>DB: BEGIN (SERIALIZABLE)
    ORD->>DB: INSERT order_fulfillment (uq idempotency_key)
    ORD->>DB: UPDATE order_line SET quantity_consumed = quantity_consumed + q\n WHERE quantity_consumed + q <= quantity_ordered
    alt guard fails (over-consume) OR duplicate key
        DB-->>ORD: constraint violation
        ORD-->>P: 409 Conflict (or 200 replay if same key+payload)
    else success
        ORD->>DB: write outbox: OrderLinesConsumed
        ORD->>DB: COMMIT
        ORD-->>P: 200 { lineStatus, remaining }
        ORD->>SB: publish OrderLinesConsumed (async relay)
        SB->>ELG: decrement coverage_limit.consumed_value
    end
```

- **Atomicity**: fulfillment insert + line update + outbox insert in one transaction.
- **Idempotency**: `UNIQUE(idempotency_key)`; a replay with the same key returns the original result (stored response), never a second consume.
- **No over/duplicate use**: guarded conditional `UPDATE` + `CHECK (quantity_consumed <= quantity_ordered)`.
- Limit decrement in eligibility is **eventually consistent** via `OrderLinesConsumed`; the *authoritative* usage record is the fulfillment row, so a lagging eligibility cache can never cause double-spend of the order itself.

### 8.2 Approval saga (order requires authorization)

```mermaid
sequenceDiagram
    participant ORD as orders-service
    participant APR as approvals-service
    participant SB as RabbitMQ/NATS

    ORD->>SB: OrderRequested (requiresAuth=true) -> status PendingApproval
    SB->>APR: consume -> create AUTHORIZATION (Requested)
    APR->>APR: Approver decides -> AuthorizationDecision
    APR->>SB: AuthorizationDecided (Approve|Reject)
    SB->>ORD: consume
    alt Approved
        ORD->>ORD: status -> Approved -> Active
    else Rejected
        ORD->>ORD: status -> Rejected (compensation: notify)
    end
```

This is a **choreographed saga** (no central orchestrator). Compensation on rejection = state rollback + notification; no partial data to undo because consume can't happen before `Active`. The same pattern governs prescription approval.

### 8.3 Consistency summary

| Concern | Mechanism |
|---|---|
| Single-service invariant (consume/dispense) | Local serializable transaction + guards + idempotency key |
| Cross-service workflow (approve → activate) | Choreographed saga via events |
| Read-model freshness (eligibility, reporting) | Eventual consistency + cache invalidation events |
| Exactly-once effect | Outbox (dedupe on publish) + idempotent consumers (dedupe on `event.id`) |

---

## 9. Caching Strategy

- **Eligibility snapshots** cached in **Valkey** keyed `elig:{beneficiaryId}:{coverageId}` with TTL + `version_hash`. Reads are cache-first; miss → recompute from policy/coverage → store. Invalidation is event-driven (`CoverageLimitChanged`, `OrderLinesConsumed`, `RxLinesDispensed`, `BeneficiaryStatusChanged`).
- **Master data** (ICD/CPT/LOINC/drug) cached read-through with long TTL; invalidated on catalog publish.
- **Beneficiary lookups** (by identifier) cached short TTL for the registration/point-of-care hot path.
- Cache is **never** the source of truth for consumption; it accelerates the *decision*, while the fulfillment/dispense tables enforce correctness.

---

## 10. Search Indexing

- **OpenSearch** powers beneficiary search (name/identifier fuzzy), provider directory, and master-data typeahead (drug/ICD); Meilisearch/Typesense are lighter alternatives at Tier 1.
- Indexes are **projections** fed by domain events into an indexer pipeline; PHI fields are **excluded or tokenized** per data-minimization rules ([18-security-model.md](18-security-model.md)).
- Reporting-service maintains denormalized read models (star-ish) refreshed by the event stream, queried for dashboards.

---

## 11. Idempotency & Concurrency

- **Idempotency-Key** header required on all unsafe, retriable POSTs (consume, dispense, decision, notification send). Stored with the response for replay (see [17-api-specifications.md](17-api-specifications.md)).
- **Optimistic concurrency** via `row_version` + `If-Match`/ETag on updates; conflicting writes get `412 Precondition Failed`.
- **Serializable isolation** only where invariants demand it (consume/dispense); everything else uses read-committed for throughput.
- **Message dedup**: consumers track processed `event.id` (Valkey set / dedup table) to make projections idempotent.

---

## 12. Multi-Tenant & Provider Isolation

- **Tenant model**: single logical tenant (Mersal) with **provider-scoped isolation** as the primary boundary — provider staff see only their own contracts, fulfillments, and dispensing.
- Enforced in three layers:
  1. **Keycloak** claims carry `provider_id` for provider users.
  2. **Kong** validates scope and injects the tenant/provider context header.
  3. **PostgreSQL RLS** policies filter rows by `provider_id`/case-worker assignment (see [18-security-model.md](18-security-model.md)).
- Beneficiary clinical data is **not** provider-partitioned (a refugee may be seen by many providers); access is governed by role + care-relationship, audited on every read.

---

## 13. Resilience Patterns

| Pattern | Where | Implementation |
|---|---|---|
| **Transactional outbox** | every publisher | events written in the same tx as state, relayed by a dispatcher (dedupe) |
| **Retry with backoff + jitter** | all consumers & downstream calls | RabbitMQ/NATS native retry + Polly policies |
| **Dead-letter queue** | RabbitMQ / NATS JetStream | poison messages parked, alerted, replayable |
| **Circuit breaker** | service-to-service sync calls | open on repeated failures, fail fast, half-open probes |
| **Bulkhead / concurrency limits** | k3s pods + connection pools | isolate noisy-neighbor load |
| **Timeouts everywhere** | HTTP + DB | prevent thread/connection exhaustion |
| **Idempotent consumers** | projections, cache invalidation | dedupe on `event.id` |
| **Health/readiness probes** | k3s | liveness, readiness, startup probes |
| **Graceful degradation** | eligibility | if cache + policy down, fall back to conservative "NeedsAuthorization" |

---

## 14. Deployment & Runtime Notes

- Each service is an independently deployable container on **k3s** (Docker Compose at Tier 1), horizontally scaled (HPA on CPU + KEDA on queue depth).
- **Self-hosted PostgreSQL** (Patroni HA) hosts schema/DB-per-service; connection is cluster-internal only with **OpenBao**-managed credentials.
- Full environment topology, networking, HA/DR in [25-deployment-architecture.md](25-deployment-architecture.md).

---

## 15. Cross-References

- Entity/relationship detail: [15-database-erd.md](15-database-erd.md)
- Endpoint contracts & idempotency headers: [17-api-specifications.md](17-api-specifications.md)
- Status transition rules referenced by sagas: [23-state-machines.md](23-state-machines.md)
- RLS/tenant/PHI enforcement: [18-security-model.md](18-security-model.md)
- claims-service design (origination, batching, adjudication, settlement): [36-claims-management.md](36-claims-management.md)
- Open-source infra realization: [25-deployment-architecture.md](25-deployment-architecture.md)
- Authoritative stack: [0C-OPEN-SOURCE-STACK.md](0C-OPEN-SOURCE-STACK.md)
