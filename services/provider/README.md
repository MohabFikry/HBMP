# provider-service

Provider network: providers, locations, contracts with agreed prices, credentialing, and the Network
Team onboarding workflow. **Release R2 — the fulfillment backbone** that phases 5 (lab/imaging) and 6
(pharmacy) route real orders through. Owns the `provider` schema. (07-functional-requirements §8 FR-NET-*.)

## THE INVARIANT — provider isolation is a hard boundary, not a filter

A provider user may **never** read, list, or infer another provider's users, locations, contracts,
queues, orders, or prescriptions. Enforced in depth: (1) `provider_id` token claim, (2) coarse RBAC at
the gateway, (3) **ABAC `provider-ownership`** at the service, (4) **PostgreSQL RLS** `provider_id`
predicate on every provider-schema row, (5) field-level minimum-necessary projection on any beneficiary
payload crossing the provider boundary. A bug in one layer must not leak — the others still deny. Tenant
separation (`tenant_id` RLS) sits above provider isolation. (Layers 3–5 land in **2b.3**.)

## Model (22-data-dictionary §5)

- `provider` — `provider_code` (UK per tenant), `legal_name`, `provider_type` {Hospital,Clinic,Lab,
  Pharmacy,Imaging}, `status` {Active,Suspended,Terminated}, `onboarding_state`. Only **Active** is routable.
- `provider_location` — one **primary** per provider (partial-unique index). *Deviation:* stored as
  `geo_lat`/`geo_lng` numerics (dev Postgres has no PostGIS); 22 §5.2 specifies `geography(Point)`.
- `provider_contract` — `contract_no` (UK), effective range; **ranges must not overlap** for one provider
  (GiST exclusion constraint on `daterange`). Multiple contracts over time are allowed.
- `contract_service_line` — `service_type` {Lab,Imaging,Consult,Procedure}, `code_system` {CPT,LOINC,
  LOCAL}, `code`, `agreed_price` (T2 financial, `≥0`), `currency_code`. Unique per (contract, system, code).
  CPT is validated against masterdata-service; LOCAL is free; LOINC is recorded (no dataset loaded yet).
- `provider_credential` — credential documents + status + expiry; mandatory credentials gate activation;
  a `ProviderCredentialExpiring` reminder fires ahead of `valid_to` (FR-NET-007).

## Endpoints (`/api/v1`, tenant-scoped)

Network Team writes (`provider:write`), Provider Admin / Network Team read (`provider:read`):

- `POST /providers` (Draft) · `GET /providers` · `GET /providers/{id}`
- `POST /providers/{id}/locations` · `POST /providers/{id}/contracts` · `POST /contracts/{id}/activate`
- `POST /contracts/{id}/service-lines` · `POST /providers/{id}/credentials`
- `GET /providers/{id}/capabilities` — derived routable catalog (Active provider + in-effect contract only);
  `agreed_price` masked unless the caller holds `provider:finance`.

Events (outbox → `provider.events`): `ProviderCreated`, `ContractActivated` (+ `ProviderStatusChanged`,
`ProviderCredentialExpiring` in 2b.2). Every mutation writes a hash-chained `audit_event`.

## Data

Migration `Infrastructure/Migrations/0001_provider_schema.sql` (needs the `btree_gist` extension for the
contract-overlap exclusion). RLS predicates arrive in `0002` (2b.3). Apply with `psql`.

## Tests

- `ProviderRulesTests` — contract overlap, in-effect window, capability derivation (only Active provider +
  in-effect contract), credential expiry/validity, mandatory-credential activation gate.
- `OnboardingWorkflowTests` — the onboarding state machine's forward guards and blocked transitions.

Endpoint wiring (CRUD, capabilities, masterdata validation, overlap 409, price masking) is exercised
against the live stack; ABAC + RLS isolation tests land in **2b.3**.
