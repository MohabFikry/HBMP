---
name: FHIR Integration Architect
description: Governs Mersal's FHIR R4 resource mapping, HL7 v2 readiness, and the versioned, scoped, audited interoperability facade with anti-corruption adapters and a DPIA gate. Use when exposing or consuming any interoperability endpoint, mapping internal entities to FHIR resources, or integrating an external party (UNHCR, government, insurer).
---

# FHIR Integration Architect

## Purpose
Keep interoperability at the **edge** — mapping happens at an API/adapter facade while internal storage stays relational and canonical. Ensure every external exposure or consumption is FHIR R4-aligned, versioned, scope-limited, audited, wrapped in an anti-corruption adapter, and preceded by a DPIA. The native `/api/v1` remains primary; a read-only FHIR facade (`/fhir/r4/*`) is layered on top.

## When to use / when not to use
- **Use when:** designing or reviewing any `/fhir/r4/*` endpoint, external data import/export, HL7 v2 ingestion/emission, mapping an internal entity to a FHIR resource, status/code translation for external partners, or onboarding an external integrator (UNHCR/gov/insurer).
- **Not for:** internal service-to-service events (Platform Architect) or the internal relational schema itself (Database Architect). This skill governs the *boundary*, not the core.

## Mersal domain knowledge & rules
**Mapping is at the API/adapter layer only; internal storage stays relational.** Canonical FHIR R4 mappings:
| Internal entity | FHIR R4 resource | Key mappings |
|---|---|---|
| `beneficiary` (+ identifiers, contacts) | **Patient** | `identifier` per type (system per NationalID/Passport/RefugeeID/UNHCRNo/MemberNo); `name`, `birthDate`, `gender`, `telecom`, `address` |
| `policy` + `coverage` + `coverage_limit` | **Coverage** | `beneficiary`→Patient; `payor`→Mersal/sponsor; `class`, `costToBeneficiary`; limits as extensions |
| `investigation_order`/`order_line` | **ServiceRequest** | `code` (CPT/LOINC), `quantityQuantity`, `status` (mapped from lifecycle), `subject`, `requester` |
| `order_fulfillment` + result | **DiagnosticReport** / **Observation** | report references ServiceRequest; result doc as `presentedForm` |
| `prescription`/`prescription_line` | **MedicationRequest** | `medicationReference`→Medication (drug), `dosageInstruction`, `dispenseRequest.quantity`, `status` |
| `dispense_event` | **MedicationDispense** | `quantity`, `whenHandedOver`, `authorizingPrescription`→MedicationRequest |
| `authorization`/`decision` | **Claim**/**ClaimResponse** or **CoverageEligibilityResponse** | pre-auth semantics |
| `referral` | **ServiceRequest** (`intent=order`, category=referral) | `performer`→to-provider |
| `encounter` | **Encounter** | `class`, `period`, `subject`, `participant` |
| `diagnosis` | **Condition** | `code` (ICD-10/11), `clinicalStatus`, `encounter` |
| `vital` | **Observation** (vital-signs) | `code` (LOINC), `valueQuantity` |
| `allergy` | **AllergyIntolerance** | `code`, `reaction`, `criticality` |
| `provider`/`provider_location` | **Organization**/**Location** | contract terms out of FHIR core scope |

- **Status translation is explicit**, never a raw enum leak. Example (ServiceRequest): Requested/PendingApproval→`draft`; Approved/Active/PartiallyUsed→`active`; Completed→`completed`; Rejected/Cancelled/Expired→`revoked`. Map every internal lifecycle to the correct FHIR status in the adapter.
- **Coding systems:** ICD-10 now (ICD-11 ready) for Condition; LOINC for labs/vitals; CPT/LOINC for ServiceRequest.code; ATC/Drug Master behind Medication. Reference master data, don't inline it.
- **Facade properties (mandatory):** **versioned** (in path; `/api/v1` primary, breaking→`/api/v2`; FHIR facade read-only to start), **scoped** (OAuth2 scopes mapped from internal RBAC permissions), and **audited** (every external read/write emits an audit event with correlation id, minimized snapshots).
- **Anti-corruption adapters** wrap each external party (UNHCR, government, insurers): translate their model to/from the canonical internal model so external quirks never leak into core services. HL7 v2 readiness = design ingestion/emission behind the same adapter seam even if not built at launch (result exchange via FHIR/HL7 is a future roadmap horizon).
- **DPIA gate:** a Data Protection Impact Assessment must be completed and approved **before** any external integration goes live, given PHI/PII/SPI (refugee/legal status) exposure. Minimize fields to the interop purpose; SPI redacted by default; PHI excluded or tokenized in anything indexable.

## Key entities, states & invariants
- Business keys (`MRS-M-*`, `ENC-*`, `ORD-*`, `RX-*`, `AUTH-*`, `REF-*`) map to FHIR `identifier`/`Resource.id`; internal UUID v7 stays internal.
- Invariants preserved across the boundary: minimum-necessary field exposure, immutable audit of every external exchange, no cross-service FK coupling introduced by integrations, provider/tenant isolation honored in scopes.
- Consume/dispense idempotency and lifecycle guards are internal-only; the facade never bypasses them (a FHIR write routes through native services and their guards).

## How to apply
- Build every external touchpoint as an adapter over the canonical model + the mapping table above; do not expose relational rows or internal enums directly.
- Translate lifecycle statuses to FHIR statuses explicitly in the adapter; keep the mapping in one place.
- Attach OAuth2 scopes derived from RBAC permissions to each facade operation; audit every call; minimize payloads to purpose.
- For a new external party, stand up an anti-corruption adapter and require a completed DPIA before go-live.
- Keep the FHIR facade read-only initially; any write path must flow through native `/api/v1` services so internal invariants/guards apply.
- In reviews, flag: internal IDs/enums leaking outward, un-scoped or un-audited endpoints, PHI/SPI in interop payloads beyond purpose, missing DPIA, adapter-less direct integrations.

## Canonical references
- FHIR R4 mapping table & status mapping: `../../17-api-specifications.md` §12
- API conventions, versioning, scopes, idempotency: `../../17-api-specifications.md` §1
- Service boundaries & event seams for adapters: `../../16-service-architecture.md`
- Compliance/DPIA, PHI/PII/SPI handling: `../../20-compliance-checklist.md`, `../../18-security-model.md`

## Guardrails
- Mapping lives at the adapter/facade layer only; internal storage stays relational and canonical.
- Every external endpoint is versioned, scoped, and audited; the FHIR facade starts read-only; writes go through native services and their guards.
- Wrap every external party in an anti-corruption adapter; never let an external model into core services.
- No external integration ships without an approved DPIA; interop payloads are minimum-necessary with SPI redacted and PHI excluded/tokenized where indexable.
- Never leak internal UUIDs or raw lifecycle enums; translate to FHIR identifiers and statuses explicitly.
