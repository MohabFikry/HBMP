# Phase 2b — Provider Network & Onboarding (Fulfillment Backbone)

**Goal:** Stand up the `provider-service` — providers, locations, contracts with agreed prices, credentialing, and provider-scoped users — plus the Network Team onboarding workflow and the **provider-isolation** enforcement that every fulfillment phase (5 lab/imaging, 6 pharmacy) depends on. Release **R2** (must land before phases 5 and 6 can route real orders).

Back to master list: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> Root `CLAUDE.md` already defines stack, conventions, security, audit, testing, and Definition of Done. This file adds phase-2b scope only.

---

## Skills to activate
> Activate `provider-network-management`, `health-insurance-tpa-operations` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [`../07-functional-requirements.md`](../07-functional-requirements.md) — **§8 Provider Network (FR-NET-001…007)** is authoritative for scope; FR-IAM-002/004/010 for provider-user provisioning and de-provisioning.
- [`../15-database-erd.md`](../15-database-erd.md) — provider domain ERD (`provider`, `provider_location`, `provider_contract`, `contract_service_line`).
- [`../22-data-dictionary.md`](../22-data-dictionary.md) — **§5 Domain: Provider** (§5.1 `provider`, §5.2 `provider_location`, §5.3 `provider_contract` / `contract_service_line`) — column names, enums, and constraints are authoritative. Note **Provider status = Active / Suspended / Terminated**.
- [`../10-role-matrix.md`](../10-role-matrix.md) — §3.13 Provider Admin (`provider:own`, no clinical read, no self-elevation), §3.14 Network Team (`tenant:own` over provider metadata, no beneficiary PHI), §7 assignment & SoD.
- [`../11-permission-matrix.md`](../11-permission-matrix.md) — ABAC `provider-ownership` (PO) condition and field-level minimization for provider-routed payloads.
- [`../18-security-model.md`](../18-security-model.md) — **§8 Provider & tenant isolation** (RLS `provider_id` predicate + ABAC PO; isolation testing) and §4 enforcement pipeline.
- [`../16-service-architecture.md`](../16-service-architecture.md) — `provider-service` placement, outbox, events. Reference: [`../32-user-stories.md`](../32-user-stories.md) US-040 (provider finds only its own authorized orders).

Master data: service-line codes reference CPT/LOINC/LOCAL via masterdata-service (phase 0b).

---

## THE INVARIANT (read before writing any code)

**Provider isolation is a hard security boundary, not a filter.** A provider user (Lab/Imaging/Pharmacy tech, Provider Admin) may NEVER read, list, or infer another provider's users, locations, contracts, queues, orders, or prescriptions. This is enforced in depth: (1) `provider_id` claim in the token, (2) coarse RBAC at the gateway, (3) **ABAC `provider-ownership`** at the service, (4) **PostgreSQL RLS** `provider_id` predicate on every provider-schema row, (5) field-level projection on any beneficiary payload that crosses the provider boundary (minimum-necessary only). A bug in any single layer must not leak — the others still deny. Every attempt to cross the boundary is denied AND audited. Tenant separation (`tenant:own`, RLS `tenant_id`) sits above this: no cross-tenant read without global break-glass.

---

## Prompts

### 2b.1 — provider-service: providers, locations, contracts, service lines

```text
Create provider-service. .NET 8 microservice, PostgreSQL schema `provider` (schema-per-service + RLS), REST /api/v1 + OpenAPI 3.1, outbox for events.

READ FIRST: ../22-data-dictionary.md §5 (all columns/enums), ../15-database-erd.md provider domain, ../07-functional-requirements.md FR-NET-001/002/006/007, ../18-security-model.md §8.

Model exactly the ../22 §5 tables (do not rename columns):
- provider(provider_id, provider_code UK, legal_name, provider_type enum {Hospital,Clinic,Lab,Pharmacy,Imaging}, status enum {Active,Suspended,Terminated}). Note the design's provider_type enum; expose "Doctor/ImagingCenter" business labels via a mapping, but persist the canonical enum.
- provider_location(location_id, provider_id FK, name, governorate, address, geo_point geography(Point), is_primary). Exactly one primary location per provider (partial unique index WHERE is_primary).
- provider_contract(contract_id, provider_id FK, contract_no UK, effective_from, effective_to nullable, status enum). A provider may hold multiple contracts over time; effective ranges must not overlap for the same provider (exclusion constraint).
- contract_service_line(service_line_id, contract_id FK, service_type enum {Lab,Imaging,Consult,Procedure}, code_system enum {CPT,LOINC,LOCAL}, code, agreed_price numeric(14,2) CHECK >= 0, currency_code char(3) ISO 4217). Unique (contract_id, code_system, code). Validate code against masterdata-service for CPT/LOINC (LOCAL is free but recorded).
- credentialing: track credential documents + status + expiry with reminder due-dates (FR-NET-007) — model as provider_credential(provider_id, credential_type, status, valid_from, valid_to, document_id). Emit a ProviderCredentialExpiring event ahead of valid_to.

Endpoints (all tenant-scoped, RLS by tenant_id; Network Team writes, Provider Admin reads own):
- CRUD /providers, /providers/{id}/locations, /providers/{id}/contracts, /contracts/{id}/service-lines, /providers/{id}/credentials.
- GET /providers/{id}/capabilities — derived catalog (which service_types + codes a provider can fulfil under an ACTIVE contract), used later by orders routing (FR-NET-006).

Status semantics: only Active providers appear as routable in /capabilities; Suspended/Terminated are excluded from routing but remain readable for audit/history (soft-delete + history, never hard delete).

Guardrails: every mutation writes an immutable hash-chained audit_event; all writes and reads of provider records are RLS-scoped to tenant_id; agreed_price is T2 (financial) — mask from roles without provider-financial permission. Emit ProviderCreated/ProviderStatusChanged/ContractActivated via outbox.

Acceptance criteria (FR-NET-001/002/006):
- Given the Network Team creates a Lab provider with a location and an active contract carrying CPT/LOINC service lines with agreed prices, When I GET /capabilities, Then the covered codes are returned only while a contract is Active and within its effective range.
- Given overlapping effective ranges for one provider's contracts, When I create the second, Then it is rejected (409).
- Given a service line with a CPT code unknown to masterdata-service, When I add it, Then it is rejected with a validation error.

Tests: unit (status/effective-range/primary-location rules), integration (CRUD + capabilities derivation + masterdata validation), RLS test (a second tenant cannot read tenant 0's providers). OpenAPI + README updated.
```

### 2b.2 — Network Team onboarding workflow

```text
Add the provider ONBOARDING WORKFLOW to provider-service, driven by the Network Team role. .NET 8, REST /api/v1.

READ FIRST: ../07-functional-requirements.md FR-NET-003/004/007, ../10-role-matrix.md §3.13/§3.14 and §7 (assignment, SoD), ../18-security-model.md §8.

Model onboarding as an explicit, auditable state machine on the provider:
Draft -> DocumentsCollected -> Credentialed -> Contracted -> Activated ; with Suspended and Terminated as post-activation states. Each transition requires the prior step complete (e.g., cannot Activate without an Active contract and non-expired mandatory credentials).

Workflow steps (Network Team):
1. Create provider (Draft) with type + legal identity.
2. Add one or more locations; mark the primary.
3. Collect onboarding documents (license, tax card, accreditation) via document-service (Blob, CMK, malware-scanned); attach as provider_credential rows with expiry.
4. Record contract + service lines with agreed prices (from 2b.1); activating a contract moves provider toward Contracted.
5. Provision provider users: create provider-scoped accounts (Provider Admin, plus role templates for Lab/Imaging/Pharmacy techs) via identity-service — EACH new user is stamped with this provider's provider_id and can ONLY be assigned provider-scoped roles for THIS provider (FR-NET-003; SoD: Provider Admin cannot self-grant clinical roles; Network Team cannot grant itself provider financial-release).
6. Activate provider once documents + credentials + active contract are present.

De-provisioning: POST /providers/{id}/suspend and /terminate immediately revoke all provider users' access across portals (FR-IAM-010) and stop order routing; both require a reason and are dual-controlled (SoD) for Terminate.

Guardrails: every onboarding action (create, add location, attach document, provision user, activate, suspend, terminate) is an immutable hash-chained audit_event with actor + justification. Credential-expiry reminders emitted (FR-NET-007). Network Team operates on provider METADATA only — no beneficiary PHI reachable from this service.

Acceptance criteria (FR-NET-004):
- Given a Draft provider, When I try to Activate it without an active contract or with an expired mandatory credential, Then activation is blocked with a clear reason.
- Given I onboard a Laboratory end-to-end (provider, location, documents, contract, users), Then it reaches Activated, appears routable in /capabilities, and every step is audited.
- Given I suspend a provider, When its users attempt to sign in or call the API, Then access is denied and order routing to it stops.

Tests: integration (full onboarding happy path + each blocked-transition guard), authz test (Network Team cannot read beneficiary clinical data), audit test (each step produces a hash-chained event).
```

### 2b.3 — Provider isolation enforcement + ABAC provider-ownership + performance metrics

```text
Wire and PROVE provider isolation across provider-service and the provider-facing reads it authorizes. This is the security core of the phase — READ THE INVARIANT SECTION of phase-2b-provider-network.md, ../18-security-model.md §8, and ../11-permission-matrix.md (ABAC PO) before coding.

Implement in depth (all layers required):
1. Token: provider users carry a provider_id claim; verify it at the gateway (coarse RBAC role->route) and reject provider tokens with no provider_id on provider routes.
2. ABAC: add/parametrize the OPA/Cerbos `provider-ownership` (PO) condition — action allowed only when resource.provider_id == subject.provider_id (and tenant matches). Return it as a reusable policy other services (orders in phase 5, pharmacy in phase 6) import so a Lab/Imaging/Pharmacy queue is filtered to the caller's provider.
3. RLS: enable PostgreSQL RLS on every provider-schema table with a provider_id (and tenant_id) predicate bound from session GUCs set per request; a buggy service query still cannot return another provider's rows.
4. Field projection: any beneficiary payload crossing the provider boundary (for later fulfillment) exposes only minimum-necessary fields; never diagnoses/notes/prescriptions beyond the permitted indication.
5. Audit: every denied cross-provider attempt emits a high-severity audit_event (actor, attempted resource, decision).

Provider performance metrics: expose read-only, provider-scoped counters (orders fulfilled, average turnaround, credential status, contract utilization) that feed reporting-service (phase 8). Metrics are computed per provider and are NOT cross-provider visible to a provider user; only Network Team / reporting sees the network-wide roll-up.

Acceptance criteria (FR-NET-005; US-040):
- Given provider A and provider B, When a user of A requests B's provider record, users, locations, contracts, queue, orders, or prescriptions, Then the response is 403/empty AND the attempt is audited — proven at BOTH the ABAC layer and the RLS layer independently.
- Given a Lab user, When they list their queue, Then only their provider's authorized lines appear and no prescriptions are ever returned (min-necessary).
- Given a provider user, When they view performance metrics, Then they see only their own provider's numbers.

REQUIRED tests (do not mark done without these):
- ISOLATION test (ABAC): user of provider A is denied every read/list/mutation targeting provider B; assert audited.
- ISOLATION test (RLS): with ABAC bypassed/mocked, a raw query under provider A's session GUC returns ZERO of provider B's rows — proving the datastore is an independent guarantee.
- CROSS-TENANT test: tenant 1 provider cannot see tenant 0 providers.
- MIN-NECESSARY test: a provider-boundary beneficiary payload contains only the whitelisted fields (no diagnoses/prescriptions).
- Reuse test: another service importing the PO policy gets the same deny for a foreign provider_id.
```

---

## Guardrails

- **Provider isolation is defense-in-depth** (token → RBAC → ABAC PO → RLS `provider_id` → field projection), each layer independently denying. Proven by separate ABAC and RLS negative tests, not one combined test.
- **Tenant separation** above provider isolation — RLS `tenant_id` on every row; no cross-tenant read without global break-glass.
- **Canonical statuses only** — provider `Active / Suspended / Terminated` (../22 §5); only Active providers are routable.
- **Minimum-necessary at the provider boundary** — provider payloads never carry diagnoses/prescriptions/unrelated EMR; agreed prices masked from roles without provider-financial permission.
- **No self-elevation / SoD** — provider users get only provider-scoped roles for their own org; Provider Admin cannot self-grant clinical roles; Terminate is dual-controlled.
- **Immutable hash-chained audit** on every onboarding action, status change, user provision/de-provision, and every denied cross-provider attempt; reads of provider PII/financial audited.
- **Soft-delete + history** — Suspended/Terminated providers remain readable for audit; never hard-deleted.

## Done when

- A Lab, Imaging, or Pharmacy provider can be onboarded end-to-end — provider record, location(s), collected documents, an active contract with priced CPT/LOINC/LOCAL service lines, and provider-scoped users — reaching **Activated** and appearing routable via `/capabilities`, with every step immutably audited (FR-NET-001/002/004/006).
- A provider user **cannot** access another provider's users/locations/contracts/queues/orders/prescriptions — proven by **independent** ABAC and RLS isolation tests plus a cross-tenant test — and every attempt is audited (FR-NET-005; US-040).
- The `provider-ownership` (PO) ABAC policy is reusable and imported by orders/pharmacy services so their queues are provider-scoped.
- Suspend/Terminate immediately revokes provider users and stops routing; provider performance metrics are provider-scoped and feed reporting.
- Isolation + cross-tenant + min-necessary tests green; OpenAPI + README updated. Global Definition of Done (root `CLAUDE.md`) met.
