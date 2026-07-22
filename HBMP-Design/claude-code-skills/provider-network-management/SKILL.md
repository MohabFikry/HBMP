---
name: Provider Network Management
description: Provider onboarding, credentialing, contracts with service lines and agreed tariffs, locations, provider-scoped users, provider isolation, and performance metrics for Mersal HBMP. Use when building or reviewing the provider network, provider contracts/tariffs, provider-admin delegation, or the provider-isolation boundary.
---

# Provider Network Management

## Purpose
Give Claude Code the Mersal provider-network model: how labs, imaging centres, pharmacies, clinics, and hospitals are onboarded, credentialed, contracted, priced, and — most importantly — **isolated** so provider staff see only their own queues and the minimum beneficiary data per task. The provider network is the fulfillment backbone that consumes orders and prescriptions and supplies the tariffs that price benefits.

## When to use / when not to use
- **Use when:** modelling provider/location records, contracts + service lines + tariffs, provider-scoped user administration, credentialing/expiry tracking, order/referral routing to providers, provider performance metrics, or enforcing provider isolation (RLS, `PO`, `OST`).
- **Do not use for:** the eligibility/authorization spine (use `health-insurance-tpa-operations`); pharmacy DUR/formulary (use `pbm-adjudication-engine`); claims settlement (use `medical-claims-engine`).

## Mersal domain knowledge & rules
- **Provider taxonomy.** `provider` (`provider_code`, `legal_name`, `provider_type ∈ {Hospital, Clinic, Lab, Pharmacy, Imaging}`, `status ∈ {Active, Suspended, Terminated}`), with `provider_location` (governorate, address, geo_point, is_primary) (`../../22-data-dictionary.md` §5.1–5.2). Routing and provider-directory search use type + capability + location.
- **Contracts, service lines, tariffs.** `provider_contract` (contract_no, effective_from/to, status) → `contract_service_line` (`service_type ∈ {Lab, Imaging, Consult, Procedure}`, `code_system ∈ {CPT, LOINC, LOCAL}`, `code`, `agreed_price`, `currency_code`) (`../../22-data-dictionary.md` §5.3). The agreed price for the matching code on an **in-window** contract is the single source of benefit pricing; there is no default price. Capability/catalog (which tests, modalities, formulary) drives correct routing (`../../07-functional-requirements.md` FR-NET-006).
- **Provider isolation — the primary boundary.** Mersal is a single logical tenant; **provider-scoped isolation** is the main partition (`../../16-service-architecture.md` §12). Enforced in three layers: (1) Entra ID claims carry `provider_id` for provider users; (2) APIM validates scope and injects provider context; (3) PostgreSQL **RLS** filters rows by `provider_id`/assignment (`../../18-security-model.md`). Provider staff see only their own contracts, fulfillments, dispensing, and queues (FR-NET-005). **No provider-side role may bulk-export beneficiary data** (`../../11-permission-matrix.md` §3.3 Export note).
- **ABAC conditions that gate provider access:** `PO` (provider-ownership: `subject.provider_id = resource.provider_id`) and `OST` (order-status gate: resource routed to the subject and status ∈ {routed, accepted, in_progress}) (`../../11-permission-matrix.md` §5). A line may be consumed **only by the provider it is routed to** (FR-INV-010). Labs/Imaging/Pharmacy read the order/prescription only under `PO`+`OST`, and even then with clinical fields minimized (Labs/Imaging see indication only; prescription = denied; pharmacy lab/imaging results denied→derived).
- **Beneficiary clinical data is NOT provider-partitioned.** A refugee may be seen by many providers, so clinical access is governed by role + active care-relationship (`TR`) and audited on every read — never by owning the beneficiary row (`../../16-service-architecture.md` §12).
- **Delegated administration.** `Provider Admin` manages **their own** staff accounts, locations, and users within the organization boundary only (`C/R/U/D 🟠PO`), and cannot touch beneficiary or clinical data (`../../11-permission-matrix.md` §3.1/§3.3; FR-NET-003). The **Network Team** (Mersal-internal) onboards, credentials, suspends, and offboards providers and manages contracts with SoD on activation (`C✅ R✅ U✅ A🟠SOD`; FR-NET-004).
- **Credentialing** tracks status and expiries with reminders (FR-NET-007); an expired credential should suspend routing to that provider.
- **Performance metrics** (roadmap-leaning): TAT to consume/result, no-show handling at clinics, dispensing throughput, result-release latency — all role-scoped and PHI-minimized in reporting (`../../07-functional-requirements.md` FR-RPT-003).

## Key entities, states & invariants
- **Entities:** `provider`, `provider_location`, `provider_contract`, `contract_service_line`, `app_user.provider_id` (identity scope), `role_binding` with `scope_provider_id` (`../../22-data-dictionary.md` §10.1).
- **Provider status:** `Active → Suspended → Terminated` (suspension/termination halts routing and consume rights).
- **Invariants:** RLS enforces `provider_id` on every provider-scoped read/write; consume/dispense allowed only under `PO`+`OST`; provider isolation cannot be widened by role, only by an audited break-glass which never grants bulk beneficiary export; contract/credential changes are audited; SoD on contract activation and provider onboarding.

## How to apply
1. Model provider records with type + locations + capability catalog; drive routing from capability + location, not free text.
2. Price every benefit line from the matching `contract_service_line.agreed_price` on an in-window contract; emit "no tariff" rather than defaulting.
3. Enforce isolation at all three layers; test that a provider login is scoped correctly (a design-time acceptance check per `../../35-implementation-plan.md` §5 migration validation).
4. Let Provider Admin manage only their own org's users/locations; let Network Team manage providers/contracts with SoD.
5. Suspend routing on credential expiry or provider suspension/termination.
6. Keep beneficiary clinical access on the care-relationship path, audited — never on provider ownership.

## Canonical references
- `../../15-database-erd.md` (provider entities & relationships)
- `../../22-data-dictionary.md` (§5 provider/contract/service-line, §10.1 identity scope)
- `../../18-security-model.md` (RLS, provider scope, break-glass)
- `../../10-role-matrix.md` (Provider Admin vs Network Team) · `../../11-permission-matrix.md` (§5 `PO`/`OST`, Export note)
- `../../16-service-architecture.md` (§12 multi-tenant & provider isolation)

## Guardrails
- Never let a provider user read another provider's queues, contracts, fulfillments, or dispensing.
- Never allow any provider-side role to bulk-export beneficiary data.
- Never let a line be consumed/dispensed by a provider it is not routed to.
- Never partition beneficiary clinical data by provider; govern it by care-relationship + audit.
- Never price a benefit outside an active contract tariff; never route to a suspended/terminated or credential-expired provider.
