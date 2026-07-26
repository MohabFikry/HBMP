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
- `branch` (14.1, design 37 §2) — the **internal Mersal facility** (org unit), NOT `provider_location`.
  `branch_code` (UK, live-row partial index), `name_en`/`name_ar`, `city`, `address`, `timezone` (default
  `Africa/Cairo`), `phone`, `opening_hours` (jsonb), `status` {Active,Suspended,Closed}. Org reference data,
  **no PHI, no tenant/provider scope** (the six branches are shared → no RLS predicate). Seeded with the six
  branches ASW/ALX/OCT/MAA/DOK/NSR (EN+AR). Staff branch-scoping is layered on in 14.2+.

## Endpoints (`/api/v1`, tenant-scoped)

Network Team writes (`provider:write`), Provider Admin / Network Team read (`provider:read`):

- `POST /providers` (Draft) · `GET /providers` · `GET /providers/{id}`
- `POST /providers/{id}/locations` · `POST /providers/{id}/contracts` · `POST /contracts/{id}/activate`
- `POST /contracts/{id}/service-lines` · `POST /providers/{id}/credentials`
- `GET /providers/{id}/capabilities` — derived routable catalog (Active provider + in-effect contract only);
  `agreed_price` masked unless the caller holds `provider:finance`.

### Branch registry (14.1 — internal facilities)

Reads are open to **any authenticated user** (org reference data, no PHI — they drive the branch switcher
and downstream branch-scoping); writes are Network/Org Admin (`provider:write`) and audited:

- `GET /branches?status=` · `GET /branches/{id}`
- `POST /branches` → `BranchCreated` · `PUT /branches/{id}` → `BranchUpdated`
- `POST /branches/{id}/status` (reason required) → `BranchStatusChanged`

### Onboarding workflow (2b.2 — Network Team, FR-NET-003/004/007)

Explicit, auditable state machine `Draft → DocumentsCollected → Credentialed → Contracted → Activated`
(+ `Suspended`/`Terminated`), guards in `OnboardingWorkflow`:

- `POST /providers/{id}/activate` — **blocked (422)** unless a primary location, valid mandatory
  credentials, and an active contract are all present; on success → routable, `ProviderStatusChanged`.
- `POST /providers/{id}/users` — provision a **provider-scoped** account (`provider_user`, stamped with
  this `provider_id`). **SoD** (`ProviderUserRules`): only Network Team / Provider Admin; a Provider Admin
  cannot self-grant admin; clinical roles are never provisioned here → `ProviderUserProvisioned`.
- `POST /providers/{id}/suspend` — reason required; stops routing + **revokes all provider users**
  (`ProviderUsersRevoked`, FR-IAM-010).
- `POST /providers/{id}/terminate` — **dual-controlled** (a distinct second approver) + reason; revokes users.
- `POST /providers/credentials/reminder-run?windowDays=30` — emits `ProviderCredentialExpiring` for
  credentials lapsing within the window.

Events (outbox → `provider.events`): `ProviderCreated`, `ContractActivated`, `ProviderStatusChanged`,
`ProviderUserProvisioned`, `ProviderUsersRevoked`, `ProviderCredentialExpiring`. Every mutation writes a
hash-chained `audit_event` with actor + justification. Network Team touches provider **metadata only** —
no beneficiary PHI is reachable from this service.

### Isolation enforcement (2b.3 — the security core)

Defense-in-depth, each layer denying independently:

1. **Token** — provider-scoped tokens (`provider_admin`/`lab_tech`/`imaging_tech`/`pharmacist`) **must**
   carry a `provider_id` claim; a provider token without one is rejected `403` on every `/api/v1` route
   (`ProviderAccessGuard.TokenMissingProviderId`).
2. **RBAC** — coarse `provider:read` / `provider:write` scope at the route (gateway + service).
3. **ABAC provider-ownership** — reads of a specific provider run through the platform authorization engine
   with the reusable `ProviderPolicies` bundle: a provider user acting on another provider is denied **and
   audited**. The same bundle is imported by orders (phase 5) / pharmacy (phase 6) so their queues are
   provider-scoped by the identical rule.
4. **PostgreSQL RLS** (`0003_rls.sql`) — `ENABLE` + **`FORCE ROW LEVEL SECURITY`** on every provider table
   with a `tenant_id` + `provider_id` predicate bound from session GUCs (`app.tenant_id` / `app.provider_id`,
   set per request by `RlsConnectionInterceptor` via `set_config`). A buggy query still returns zero foreign
   rows. **The app MUST connect as a non-superuser, `NOBYPASSRLS` role** (`hbmp_app`, `0004_app_role.sql`) —
   a superuser silently bypasses every policy.
5. **Minimum-necessary projection** — the only beneficiary shape crossing to a provider is
   `ProviderBoundaryPatient` (id ref, member no, initials, sex, age, ordered service+code) — never
   diagnoses/notes/prescriptions/results/contact PII. A reflection test fails the build if a forbidden term
   is ever added.

Every denied cross-provider attempt emits a high-severity `audit_event`. **Performance metrics**
(`GET /providers/{id}/metrics`) are provider-scoped (own numbers only); the network roll-up (`GET /metrics`)
is Network-Team-only. Order throughput / turnaround fields are populated by phase 5/6 fulfillment events.

## Data

Migrations under `Infrastructure/Migrations/` (apply in order with `psql`):
`0001_provider_schema.sql` (needs `btree_gist` for the contract-overlap exclusion), `0002_onboarding.sql`,
`0003_rls.sql`, `0004_app_role.sql` (provisions `hbmp_app`; ops sets its password out of band and points
`ConnectionStrings__Provider` at it).

## Tests

- `ProviderRulesTests` — contract overlap, in-effect window, capability derivation (only Active provider +
  in-effect contract), credential expiry/validity, mandatory-credential activation gate.
- `OnboardingWorkflowTests` — the onboarding state machine's forward guards and blocked transitions.
- `ProviderUserRulesTests` — provisioning SoD (no self-elevation, no clinical, role allow-list).
- `ProviderIsolationTests` — **ABAC** isolation: provider A denied every read of provider B (audited),
  allowed on own; cross-tenant denied; **PO-reuse** proof (a downstream service importing the bundle gets
  the same deny).
- `MinNecessaryTests` — reflection over `ProviderBoundaryPatient` (no clinical/PII field can exist).
- `RlsIsolationTests` — **RLS** isolation proven at the datastore, independently of ABAC (env-gated:
  `PROVIDER_TEST_DB_OWNER` + `PROVIDER_TEST_DB_APP`; the app conn string must be the `NOBYPASSRLS` role).

Endpoint wiring (CRUD, capabilities, masterdata validation, overlap 409, price masking) is exercised
against the live stack.
