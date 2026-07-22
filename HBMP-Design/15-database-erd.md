# 15 — Database ERD (Logical Data Model)

> Part of the **Mersal Healthcare Benefit Management Platform (HBMP)** design workspace.
> Up: [00-README-INDEX.md](00-README-INDEX.md) · Foundations: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [16-service-architecture.md](16-service-architecture.md) · [17-api-specifications.md](17-api-specifications.md) · [22-data-dictionary.md](22-data-dictionary.md) · [23-state-machines.md](23-state-machines.md) · [18-security-model.md](18-security-model.md)

---

## 1. Purpose & Scope

This document defines the **full logical data model** for HBMP. It is the authoritative reference for:

- Entities, attributes (conceptual), keys, and relationships/cardinalities.
- Normalization decisions (target **3NF**, with pragmatic denormalization for read models).
- Indexing strategy for the hot paths (eligibility check, order-consume, dispense).
- **Soft-delete + history** strategy for auditability.
- Mapping of tables to **service-owned PostgreSQL schemas** (schema/DB-per-service).

The physical column-level detail (types, nullability, validation, PII/PHI classification) lives in [22-data-dictionary.md](22-data-dictionary.md). State transitions live in [23-state-machines.md](23-state-machines.md). This file focuses on **structure and relationships**.

> **Modeling principle.** Each microservice **owns its schema**. Cross-service references are stored as *identifiers/values* (e.g., `beneficiary_id UUID`), **not** as enforced foreign keys across service boundaries. FK constraints shown in the diagrams are enforced **only within a single service schema**. Cross-schema links are drawn as dashed/logical relationships and are maintained via events + eventual consistency (see [16-service-architecture.md](16-service-architecture.md)).

---

## 2. Schema Ownership Map

| Schema (DB/service) | Owning service | Core entities |
|---|---|---|
| `identity` | identity/auth | `app_user`, `role`, `permission`, `role_permission`, `user_role` |
| `patient` | patient-service | `beneficiary`, `beneficiary_identifier`, `contact`, `family_group`, `dependent_link`, `beneficiary_history` |
| `policy` | policy-service | `policy`, `coverage`, `coverage_limit`, `benefit_category` |
| `eligibility` | eligibility-service | `eligibility_snapshot`, `eligibility_rule` (read-optimized) |
| `provider` | provider-service | `provider`, `provider_location`, `provider_contract`, `contract_service_line` |
| `emr` | emr-service | `appointment`, `encounter`, `emr_note`, `diagnosis`, `vital`, `allergy`, `medication_history` |
| `orders` | orders-service | `investigation_order`, `order_line`, `order_fulfillment` |
| `pharmacy` | pharmacy-service | `prescription`, `prescription_line`, `dispense_event` |
| `approvals` | approvals-service | `authorization`, `authorization_decision`, `referral`, `referral_event` |
| `notification` | notification-service | `notification`, `notification_template`, `delivery_attempt` |
| `masterdata` | reference (shared read) | `icd_code`, `cpt_code`, `loinc_code`, `drug`, `atc_class`, `drug_interaction`, `allergen` |
| `audit` | audit-service | `audit_event` (append-only, per-service partitions) |
| `document` | document-service | `document`, `document_version` (metadata; blobs in Object Storage) |

> **History tables** live in the same schema as their base table, suffixed `_history` (e.g., `patient.beneficiary_history`). They are written by triggers/outbox, never by application logic directly.

---

## 3. Conventions Used in the ERDs

- **PK** is a surrogate `*_id` (UUID **v7** for time-orderable clustering) unless the entity is pure reference data keyed by a natural code (`icd_code.code`).
- **Business keys** are stored as unique, human-facing identifiers: `MRS-M-*` (member), `ENC-*`, `ORD-*`, `RX-*`, `AUTH-*`, `REF-*`.
- Every mutable transactional table carries the standard audit columns: `created_at`, `created_by`, `updated_at`, `updated_by`, `row_version` (optimistic concurrency), `is_deleted` (soft delete), `deleted_at`, `deleted_by`.
- Money is `NUMERIC(14,2)` + ISO `currency_code`; quantities `NUMERIC(14,3)`.
- Enumerations are modeled as constrained `TEXT`/`VARCHAR` with `CHECK` + a companion lookup table where operationally useful.

---

## 4. Domain ERD — Patient / Beneficiary

```mermaid
erDiagram
    BENEFICIARY ||--o{ BENEFICIARY_IDENTIFIER : "has"
    BENEFICIARY ||--o{ CONTACT : "has"
    BENEFICIARY ||--o{ DEPENDENT_LINK : "guardian_of"
    FAMILY_GROUP ||--o{ DEPENDENT_LINK : "groups"
    BENEFICIARY ||--o{ BENEFICIARY_HISTORY : "versioned_as"
    BENEFICIARY ||--o{ DOCUMENT : "logical_ref"

    BENEFICIARY {
        uuid beneficiary_id PK "UUID v7"
        string member_no UK "MRS-M-*"
        string given_name
        string family_name
        date   birth_date
        string sex
        string nationality_code
        string status "Pending|Active|Suspended|Expired|Blocked|Inactive"
        uuid   family_group_id FK "nullable"
        bool   is_deleted
        int    row_version
        timestamptz created_at
        timestamptz updated_at
    }

    BENEFICIARY_IDENTIFIER {
        uuid identifier_id PK
        uuid beneficiary_id FK
        string identifier_type "NationalID|Passport|RefugeeID|UNHCRNo|MemberNo"
        string identifier_value
        string issuing_country
        date   valid_from
        date   valid_to
        bool   is_primary
    }

    CONTACT {
        uuid contact_id PK
        uuid beneficiary_id FK
        string contact_type "Phone|Email|Address|EmergencyContact"
        string value
        string preferred_channel
        bool   is_primary
    }

    FAMILY_GROUP {
        uuid family_group_id PK
        string family_code UK
        uuid   head_beneficiary_id "logical FK"
    }

    DEPENDENT_LINK {
        uuid dependent_link_id PK
        uuid family_group_id FK
        uuid guardian_beneficiary_id FK
        uuid dependent_beneficiary_id "logical FK"
        string relationship "Child|Spouse|Parent|Other"
    }

    BENEFICIARY_HISTORY {
        uuid history_id PK
        uuid beneficiary_id FK
        int  row_version
        jsonb snapshot
        string change_type "INSERT|UPDATE|SOFT_DELETE"
        timestamptz valid_from
        timestamptz valid_to
        uuid changed_by
    }
```

**Notes**

- `BENEFICIARY_IDENTIFIER` is a separate table (not columns on `beneficiary`) to satisfy **3NF** — a beneficiary may hold multiple identifier types over time, each with its own issuing authority and validity window. Uniqueness enforced by `UNIQUE (identifier_type, identifier_value)` (partial, `WHERE is_deleted = false`).
- `CONTACT` is normalized 1:N; `preferred_channel` drives notification routing.
- Guardianship/dependents are modeled via `DEPENDENT_LINK` to support many-to-many family relationships without duplicating beneficiary rows (a person can be both a dependent in one link and a guardian in another).
- **PII/PHI**: nearly every column here is PII; see field-level classification in [22-data-dictionary.md](22-data-dictionary.md). RLS restricts row access by tenant/case-worker assignment.

---

## 5. Domain ERD — Policy / Coverage / Eligibility

```mermaid
erDiagram
    POLICY ||--o{ COVERAGE : "contains"
    COVERAGE ||--o{ COVERAGE_LIMIT : "constrains"
    BENEFIT_CATEGORY ||--o{ COVERAGE : "typed_by"
    COVERAGE ||--o{ ELIGIBILITY_SNAPSHOT : "materialized_into"

    POLICY {
        uuid policy_id PK
        string policy_no UK
        string sponsor "Mersal program/donor"
        date   effective_from
        date   effective_to
        string status "Active|Suspended|Expired"
    }

    COVERAGE {
        uuid coverage_id PK
        uuid policy_id FK
        uuid beneficiary_id "logical FK"
        uuid benefit_category_id FK
        date effective_from
        date effective_to
        string status
    }

    BENEFIT_CATEGORY {
        uuid benefit_category_id PK
        string code UK "e.g., LAB|IMAGING|PHARMACY|CONSULT|REFERRAL"
        string name
    }

    COVERAGE_LIMIT {
        uuid coverage_limit_id PK
        uuid coverage_id FK
        string limit_type "Annual|PerEncounter|Lifetime|Count"
        numeric limit_value
        numeric consumed_value
        string  currency_code
        string  reset_period "None|Monthly|Quarterly|Yearly"
    }

    ELIGIBILITY_SNAPSHOT {
        uuid snapshot_id PK
        uuid beneficiary_id "logical FK"
        uuid coverage_id FK
        string decision "Eligible|Ineligible|NeedsAuthorization"
        jsonb limit_state "denormalized limits"
        timestamptz computed_at
        timestamptz expires_at
        string version_hash
    }
```

**Notes**

- `COVERAGE_LIMIT.consumed_value` is the **authoritative accumulator** for benefit consumption; updates are transactional and serialized (see order-consume saga in [16-service-architecture.md](16-service-architecture.md)).
- `ELIGIBILITY_SNAPSHOT` is a **read-optimized denormalization** of policy + coverage + limits, cached in Valkey and invalidated by policy/consumption events. It intentionally violates strict 3NF for read performance; it is a *derived* materialization, not a source of truth. `version_hash` + `expires_at` guard staleness.

---

## 6. Domain ERD — Provider

```mermaid
erDiagram
    PROVIDER ||--o{ PROVIDER_LOCATION : "operates"
    PROVIDER ||--o{ PROVIDER_CONTRACT : "signs"
    PROVIDER_CONTRACT ||--o{ CONTRACT_SERVICE_LINE : "prices"

    PROVIDER {
        uuid provider_id PK
        string provider_code UK
        string legal_name
        string provider_type "Hospital|Clinic|Lab|Pharmacy|Imaging"
        string status "Active|Suspended|Terminated"
    }

    PROVIDER_LOCATION {
        uuid location_id PK
        uuid provider_id FK
        string name
        string governorate
        string address
        geography geo_point
        bool   is_primary
    }

    PROVIDER_CONTRACT {
        uuid contract_id PK
        uuid provider_id FK
        string contract_no UK
        date   effective_from
        date   effective_to
        string status
    }

    CONTRACT_SERVICE_LINE {
        uuid service_line_id PK
        uuid contract_id FK
        string service_type "Lab|Imaging|Consult|Procedure"
        string code_system "CPT|LOINC|LOCAL"
        string code
        numeric agreed_price
        string currency_code
    }
```

**Notes**

- Provider isolation (multi-tenant): all provider-scoped reads are filtered by `provider_id` via RLS using the caller's provider claim (see [18-security-model.md](18-security-model.md)).
- `CONTRACT_SERVICE_LINE` links priced services to standard code systems, enabling automated adjudication.

---

## 7. Domain ERD — EMR / Clinical

```mermaid
erDiagram
    APPOINTMENT ||--o| ENCOUNTER : "realized_as"
    ENCOUNTER ||--o{ EMR_NOTE : "documents"
    ENCOUNTER ||--o{ DIAGNOSIS : "records"
    ENCOUNTER ||--o{ VITAL : "measures"
    BENEFICIARY ||--o{ ALLERGY : "logical_ref"
    BENEFICIARY ||--o{ MEDICATION_HISTORY : "logical_ref"
    DIAGNOSIS }o--|| ICD_CODE : "coded_by"

    APPOINTMENT {
        uuid appointment_id PK
        uuid beneficiary_id "logical FK"
        uuid provider_id "logical FK"
        uuid location_id "logical FK"
        timestamptz scheduled_start
        timestamptz scheduled_end
        string status "Booked|CheckedIn|Completed|NoShow|Cancelled"
    }

    ENCOUNTER {
        uuid encounter_id PK
        string encounter_no UK "ENC-*"
        uuid beneficiary_id "logical FK"
        uuid appointment_id FK "nullable"
        uuid provider_id "logical FK"
        string encounter_class "Ambulatory|Emergency|Inpatient|Virtual"
        timestamptz started_at
        timestamptz ended_at
        string status "InProgress|Finished|Cancelled"
    }

    EMR_NOTE {
        uuid note_id PK
        uuid encounter_id FK
        string note_type "SOAP|Progress|Nursing"
        text subjective
        text objective
        text assessment
        text plan
        uuid authored_by
        timestamptz authored_at
        bool is_signed
    }

    DIAGNOSIS {
        uuid diagnosis_id PK
        uuid encounter_id FK
        string icd_code FK
        string diagnosis_rank "Primary|Secondary"
        string clinical_status "Active|Resolved|Recurrence"
        timestamptz recorded_at
    }

    VITAL {
        uuid vital_id PK
        uuid encounter_id FK
        string vital_type "BP|HR|Temp|SpO2|Weight|Height|BMI"
        numeric value_num
        string unit
        string loinc_code "nullable, LOINC-ready"
        timestamptz measured_at
    }

    ALLERGY {
        uuid allergy_id PK
        uuid beneficiary_id "logical FK"
        uuid allergen_id "FK masterdata.allergen"
        string reaction
        string severity "Mild|Moderate|Severe"
        string status "Active|Inactive|Resolved"
    }

    MEDICATION_HISTORY {
        uuid med_history_id PK
        uuid beneficiary_id "logical FK"
        uuid drug_id "FK masterdata.drug"
        string source "Prescribed|SelfReported|External"
        date start_date
        date end_date
        string status "Active|Stopped"
    }

    ICD_CODE {
        string code PK "ICD-10 (ICD-11 ready)"
        string title
    }
```

**Notes**

- `EMR_NOTE` implements the **SOAP** structure as first-class columns for structured querying and FHIR `Composition`/`ClinicalImpression` alignment.
- `DIAGNOSIS.icd_code` is an intra-schema FK **only if** master data is replicated into the EMR schema; in the canonical model `masterdata` is a separate read-shared schema, so this is a validated logical reference (validated at write via master-data lookup/cache).
- `VITAL.loinc_code` is nullable now, populated as LOINC coding is rolled out ("LOINC-ready").

---

## 8. Domain ERD — Investigation Orders (Core Invariant Domain)

```mermaid
erDiagram
    INVESTIGATION_ORDER ||--o{ ORDER_LINE : "itemizes"
    ORDER_LINE ||--o{ ORDER_FULFILLMENT : "consumed_by"
    INVESTIGATION_ORDER ||--o{ INVESTIGATION_ORDER_HISTORY : "versioned_as"

    INVESTIGATION_ORDER {
        uuid order_id PK
        string order_no UK "ORD-*"
        uuid beneficiary_id "logical FK"
        uuid encounter_id "logical FK"
        uuid ordering_provider_id "logical FK"
        uuid authorization_id "logical FK, nullable"
        string order_type "Lab|Imaging|Procedure"
        string status "Requested|PendingApproval|Approved|Rejected|Active|PartiallyUsed|Completed|Expired|Cancelled"
        timestamptz requested_at
        timestamptz expires_at
        int row_version
        bool is_deleted
    }

    ORDER_LINE {
        uuid order_line_id PK
        uuid order_id FK
        string code_system "CPT|LOINC|LOCAL"
        string code
        string description
        numeric quantity_ordered
        numeric quantity_consumed
        string status "Active|PartiallyUsed|Completed|Cancelled"
        int row_version
    }

    ORDER_FULFILLMENT {
        uuid fulfillment_id PK
        uuid order_line_id FK
        uuid performing_provider_id "logical FK"
        numeric quantity
        string idempotency_key UK "prevents duplicate consume"
        uuid result_document_id "logical FK, nullable"
        timestamptz consumed_at
        uuid consumed_by
    }

    INVESTIGATION_ORDER_HISTORY {
        uuid history_id PK
        uuid order_id FK
        int row_version
        jsonb snapshot
        string change_type
        timestamptz valid_from
        timestamptz valid_to
    }
```

**Invariants enforced structurally + transactionally** (see [23-state-machines.md](23-state-machines.md)):

1. **Atomic + idempotent consume.** Each consume writes exactly one `ORDER_FULFILLMENT` row inside a serializable transaction that also increments `ORDER_LINE.quantity_consumed`. The unique `idempotency_key` guarantees replays are no-ops.
2. **No over-consumption.** `CHECK (quantity_consumed <= quantity_ordered)` on `ORDER_LINE` plus a guarded `UPDATE ... WHERE quantity_consumed + :q <= quantity_ordered` makes duplicate/over usage impossible.
3. **No reuse.** Fulfillment rows are append-only; there is no update path that "returns" quantity.
4. **Partial fulfillment.** `PartiallyUsed` derived when `0 < quantity_consumed < quantity_ordered`.
5. **Full audit.** Every state change emits an `audit_event` and a `_history` row.

**Indexes:** `idx_order_beneficiary (beneficiary_id, status)`, `idx_orderline_order (order_id)`, `uq_fulfillment_idem (idempotency_key)`, `idx_order_expiry (expires_at) WHERE status IN ('Active','PartiallyUsed')` for expiry sweeps.

---

## 9. Domain ERD — Pharmacy / Prescription

```mermaid
erDiagram
    PRESCRIPTION ||--o{ PRESCRIPTION_LINE : "itemizes"
    PRESCRIPTION_LINE ||--o{ DISPENSE_EVENT : "dispensed_by"
    PRESCRIPTION_LINE }o--|| DRUG : "for_drug"

    PRESCRIPTION {
        uuid prescription_id PK
        string rx_no UK "RX-*"
        uuid beneficiary_id "logical FK"
        uuid encounter_id "logical FK"
        uuid prescriber_id "logical FK"
        uuid authorization_id "logical FK, nullable"
        string status "Draft|Submitted|Approved|Rejected|PartiallyDispensed|Dispensed|Expired|Cancelled"
        timestamptz submitted_at
        timestamptz expires_at
        int row_version
    }

    PRESCRIPTION_LINE {
        uuid prescription_line_id PK
        uuid prescription_id FK
        uuid drug_id FK
        string dose
        string route
        string frequency
        numeric quantity_prescribed
        numeric quantity_dispensed
        int refills_allowed
        string status "Active|PartiallyDispensed|Dispensed|Cancelled"
    }

    DISPENSE_EVENT {
        uuid dispense_id PK
        uuid prescription_line_id FK
        uuid dispensing_pharmacy_id "logical FK"
        numeric quantity
        string idempotency_key UK
        string batch_no
        timestamptz dispensed_at
        uuid dispensed_by
    }

    DRUG {
        uuid drug_id PK
        string drug_code UK
        string name
        string atc_code "FK atc_class"
        string form
        string strength
    }
```

**Notes**

- `DISPENSE_EVENT` mirrors `ORDER_FULFILLMENT` exactly: append-only, idempotency-keyed, guarded quantity update, so the **same consume invariants** apply to dispensing.
- Drug interaction / allergy checks run at prescribe time against `masterdata.drug_interaction` and `emr.allergy`.

---

## 10. Domain ERD — Authorizations & Referrals

```mermaid
erDiagram
    AUTHORIZATION ||--o{ AUTHORIZATION_DECISION : "decided_by"
    REFERRAL ||--o{ REFERRAL_EVENT : "tracked_by"

    AUTHORIZATION {
        uuid authorization_id PK
        string auth_no UK "AUTH-*"
        uuid beneficiary_id "logical FK"
        string requested_for "Order|Prescription|Referral"
        uuid subject_ref "order/rx/referral id"
        string status "Draft|Submitted|UnderReview|Approved|PartiallyApproved|Rejected|InfoRequested|Overridden|EmergencyApproved|Expired"
        timestamptz requested_at
        timestamptz decided_at
        timestamptz expires_at
    }

    AUTHORIZATION_DECISION {
        uuid decision_id PK
        uuid authorization_id FK
        string decision "Approve|Reject|RequestInfo"
        text  rationale
        uuid  decided_by
        timestamptz decided_at
        jsonb applied_limits
    }

    REFERRAL {
        uuid referral_id PK
        string referral_no UK "REF-*"
        uuid beneficiary_id "logical FK"
        uuid from_provider_id "logical FK"
        uuid to_provider_id "logical FK"
        string specialty
        string status "Requested|Accepted|Scheduled|Completed|Rejected|Cancelled|Expired"
        timestamptz requested_at
    }

    REFERRAL_EVENT {
        uuid referral_event_id PK
        uuid referral_id FK
        string event_type
        jsonb payload
        timestamptz occurred_at
    }
```

**Notes**

- `AUTHORIZATION.subject_ref` is a polymorphic logical reference typed by `requested_for`; integrity is maintained by the approvals-service and validated via events, not cross-schema FK.
- `AUTHORIZATION_DECISION` is append-only; the current decision is the latest by `decided_at`. This preserves the full decision trail for audit/appeals.

---

## 11. Domain ERD — Identity / RBAC

```mermaid
erDiagram
    APP_USER ||--o{ USER_ROLE : "assigned"
    ROLE ||--o{ USER_ROLE : "grants"
    ROLE ||--o{ ROLE_PERMISSION : "includes"
    PERMISSION ||--o{ ROLE_PERMISSION : "granted_in"

    APP_USER {
        uuid user_id PK
        string external_oid UK "Keycloak subject id"
        string display_name
        string email
        uuid   provider_id "logical FK, nullable (provider-scoped users)"
        string status "Active|Disabled"
    }

    ROLE {
        uuid role_id PK
        string code UK "Physician|Pharmacist|CaseWorker|Approver|Admin|Auditor"
        string name
    }

    PERMISSION {
        uuid permission_id PK
        string code UK "orders:consume|rx:dispense|auth:decide|..."
        string description
    }

    USER_ROLE {
        uuid user_role_id PK
        uuid user_id FK
        uuid role_id FK
        uuid scope_provider_id "nullable"
    }

    ROLE_PERMISSION {
        uuid role_permission_id PK
        uuid role_id FK
        uuid permission_id FK
    }
```

**Notes**

- Authentication delegates to **Keycloak**; `app_user.external_oid` links the local record to the IdP subject. Local RBAC tables drive fine-grained permissions/scopes mapped to OAuth2 scopes in [17-api-specifications.md](17-api-specifications.md).

---

## 12. Cross-Cutting — Documents, Notifications, Audit

```mermaid
erDiagram
    DOCUMENT ||--o{ DOCUMENT_VERSION : "versioned_as"
    NOTIFICATION_TEMPLATE ||--o{ NOTIFICATION : "renders"
    NOTIFICATION ||--o{ DELIVERY_ATTEMPT : "delivered_via"

    DOCUMENT {
        uuid document_id PK
        string doc_type "LabResult|ImagingReport|Consent|IDScan|Referral"
        uuid owner_beneficiary_id "logical FK"
        string classification "PHI|PII|Internal"
        string blob_container
        string current_version_no
        bool   is_deleted
    }

    DOCUMENT_VERSION {
        uuid version_id PK
        uuid document_id FK
        string version_no
        string blob_path
        string checksum_sha256
        bigint size_bytes
        timestamptz uploaded_at
        uuid uploaded_by
    }

    NOTIFICATION {
        uuid notification_id PK
        uuid template_id FK
        uuid recipient_beneficiary_id "logical FK, nullable"
        uuid recipient_user_id "logical FK, nullable"
        string channel "SMS|Email|Push|InApp"
        string status "Queued|Sent|Delivered|Failed"
        jsonb payload
        timestamptz created_at
    }

    NOTIFICATION_TEMPLATE {
        uuid template_id PK
        string code UK
        string channel
        string locale
        text   body_template
    }

    DELIVERY_ATTEMPT {
        uuid attempt_id PK
        uuid notification_id FK
        int  attempt_no
        string result "Success|Retryable|Permanent"
        text  provider_response
        timestamptz attempted_at
    }

    AUDIT_EVENT {
        uuid audit_event_id PK
        string service_name
        string entity_type
        uuid   entity_id
        string action "CREATE|UPDATE|SOFT_DELETE|STATE_CHANGE|CONSUME|DISPENSE|DECISION"
        uuid   actor_user_id
        jsonb  before_state
        jsonb  after_state
        string correlation_id
        timestamptz occurred_at
    }
```

**Notes**

- **Blobs never live in the RDBMS.** `DOCUMENT`/`DOCUMENT_VERSION` hold metadata + checksum + object path; content is in MinIO (S3-compatible, SSE). `checksum_sha256` supports integrity verification.
- `AUDIT_EVENT` is **append-only**, range-partitioned by month, one logical stream fed by every service's outbox. `correlation_id` ties a business flow (e.g., order → approval → consume) across services.

---

## 13. Master Data ERD

```mermaid
erDiagram
    ATC_CLASS ||--o{ DRUG : "classifies"
    DRUG ||--o{ DRUG_INTERACTION : "interacts_a"
    DRUG ||--o{ DRUG_INTERACTION : "interacts_b"

    ICD_CODE {
        string code PK
        string title
        string chapter
        bool   is_billable
        string icd11_map "ICD-11 ready"
    }
    CPT_CODE {
        string code PK
        string description
        string category
    }
    LOINC_CODE {
        string code PK
        string long_name
        string component
        string property
    }
    ATC_CLASS {
        string atc_code PK
        string title
        int    level
    }
    DRUG {
        uuid drug_id PK
        string drug_code UK
        string name
        string atc_code FK
        string form
        string strength
    }
    DRUG_INTERACTION {
        uuid interaction_id PK
        uuid drug_a_id FK
        uuid drug_b_id FK
        string severity "Minor|Moderate|Major|Contraindicated"
        text   description
    }
    ALLERGEN {
        uuid allergen_id PK
        string code UK
        string name
        string category "Drug|Food|Environmental"
    }
```

**Notes**

- Master data is **versioned and read-mostly**; distributed to services via cache + OpenSearch index. `icd_code.icd11_map` future-proofs the ICD-10 → ICD-11 migration.

---

## 14. Normalization Rationale (to 3NF)

| Decision | Normal form driver |
|---|---|
| Identifiers split into `beneficiary_identifier` | Removes repeating groups (multiple ID types) → **1NF/2NF**. |
| Contacts, allergies, vitals as child tables | Each depends on the full PK of its parent, not partial → **2NF**. |
| `benefit_category` extracted from `coverage` | Category name/attributes depend on category, not coverage → **3NF** (no transitive dependency). |
| `atc_class` extracted from `drug` | ATC title depends on `atc_code`, not `drug_id` → **3NF**. |
| Decisions/fulfillments/dispense as append-only child tables | Preserves history without update anomalies. |
| `eligibility_snapshot`, `coverage_limit.consumed_value` | **Deliberate denormalization** for read/consume hot paths — documented as derived/accumulator, not source duplication. |

---

## 15. Soft-Delete & History Strategy

- **Soft delete:** transactional tables carry `is_deleted`, `deleted_at`, `deleted_by`. All read paths filter `is_deleted = false`; enforced via **RLS predicates** and default views. Unique indexes are **partial** (`WHERE is_deleted = false`) so a deleted row's business key can be reused if policy allows.
- **History:** each mutable base table has a `_history` twin populated by an **AFTER INSERT/UPDATE/DELETE trigger** (or the transactional outbox writer). History rows are immutable and hold a `jsonb snapshot` plus `valid_from`/`valid_to` (system-time temporal). This yields point-in-time reconstruction without SQL:2011 temporal-table dependence.
- **Audit vs history:** `_history` = per-entity temporal record (what the row looked like); `audit_event` = per-action security/compliance log (who did what, correlation across services). Both are required; they serve different auditors.

---

## 16. Key Indexing Summary (Hot Paths)

| Path | Table(s) | Index |
|---|---|---|
| Eligibility check | `eligibility_snapshot` | `(beneficiary_id, coverage_id) INCLUDE (decision, expires_at)` |
| Order consume | `order_line`, `order_fulfillment` | guarded update on `(order_line_id)`, `UNIQUE(idempotency_key)` |
| Dispense | `dispense_event` | `UNIQUE(idempotency_key)`, `(prescription_line_id)` |
| Beneficiary lookup | `beneficiary_identifier` | `(identifier_type, identifier_value) WHERE is_deleted=false` |
| Encounter timeline | `encounter` | `(beneficiary_id, started_at DESC)` |
| Expiry sweeps | `investigation_order`, `prescription` | partial index on `expires_at WHERE status active-ish` |
| Audit query | `audit_event` | `(entity_type, entity_id, occurred_at)`, monthly partitions |

---

## 17. Cross-References

- Transitions & guards for every `status` column: [23-state-machines.md](23-state-machines.md)
- Event choreography for cross-schema consistency: [16-service-architecture.md](16-service-architecture.md)
- Field-level types/PII/validation: [22-data-dictionary.md](22-data-dictionary.md)
- RLS, LUKS + pgcrypto at rest, OpenBao-managed keys, data minimization: [18-security-model.md](18-security-model.md)
