# 22 — Data Dictionary

> Part of the **Mersal Healthcare Benefit Management Platform (HBMP)** design workspace.
> Up: [00-README-INDEX.md](00-README-INDEX.md) · Foundations: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [15-database-erd.md](15-database-erd.md) · [16-service-architecture.md](16-service-architecture.md) · [17-api-specifications.md](17-api-specifications.md) · [23-state-machines.md](23-state-machines.md) · [18-security-model.md](18-security-model.md)

---

## 1. Purpose & Legend

Field-level reference for the core HBMP entities defined in [15-database-erd.md](15-database-erd.md). Types are PostgreSQL. Sensitivity classification drives RLS, masking, and data-minimization ([18-security-model.md](18-security-model.md)).

**Sensitivity classes**

| Class | Meaning | Handling |
|---|---|---|
| **PHI** | Protected health info (clinical) | Encrypted at rest (LUKS full-disk + pgcrypto column-level), RLS, audit on read, minimized in search/exports |
| **PII** | Personal identifying info | Encrypted at rest, RLS, masked in non-prod |
| **SPI** | Sensitive personal (refugee/legal status) | Strictest access, redacted by default |
| **Internal** | Business/operational, non-personal | Standard controls |
| **Public** | Reference/master data | Cacheable, broadly readable |

**Common audit columns** (present on all mutable transactional tables, omitted from per-table lists for brevity):

| Column | Type | Null | Description | Sens |
|---|---|---|---|---|
| `created_at` | `timestamptz` | No | Row creation (UTC) | Internal |
| `created_by` | `uuid` | No | Actor user id | Internal |
| `updated_at` | `timestamptz` | Yes | Last update | Internal |
| `updated_by` | `uuid` | Yes | Last actor | Internal |
| `row_version` | `integer` | No | Optimistic concurrency (ETag) | Internal |
| `is_deleted` | `boolean` | No | Soft-delete flag (default false) | Internal |
| `deleted_at` | `timestamptz` | Yes | Soft-delete time | Internal |
| `deleted_by` | `uuid` | Yes | Soft-delete actor | Internal |

---

## 2. Domain: Patient (`patient` schema)

### 2.1 `beneficiary`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `beneficiary_id` | `uuid` | No | PK | UUID v7 | Internal | v7 generated |
| `member_no` | `varchar(20)` | No | UK | Business key `MRS-M-YYYY-NNNNNN` | PII | regex `^MRS-M-\d{4}-\d{6}$` |
| `given_name` | `varchar(100)` | No | | First name | PII | non-empty |
| `family_name` | `varchar(100)` | No | | Last name | PII | non-empty |
| `birth_date` | `date` | No | | Date of birth | PII | ≤ today |
| `sex` | `varchar(10)` | No | | Biological sex | PII | enum: male/female/other/unknown |
| `nationality_code` | `char(3)` | Yes | | ISO 3166-1 alpha-3 | PII | ISO code |
| `status` | `varchar(16)` | No | | Lifecycle status | Internal | enum (see §11) |
| `family_group_id` | `uuid` | Yes | FK | Family group | Internal | FK patient.family_group |

Indexes: PK; `UNIQUE(member_no) WHERE is_deleted=false`; `(family_name, given_name)`; `(status)`.

### 2.2 `beneficiary_identifier`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `identifier_id` | `uuid` | No | PK | | Internal | |
| `beneficiary_id` | `uuid` | No | FK | Owner | Internal | FK beneficiary |
| `identifier_type` | `varchar(16)` | No | | Type | PII | enum (see §11) |
| `identifier_value` | `varchar(64)` | No | | The value | **SPI** | non-empty; type-specific format |
| `issuing_country` | `char(3)` | Yes | | Issuer country | PII | ISO code |
| `valid_from` | `date` | Yes | | Validity start | Internal | |
| `valid_to` | `date` | Yes | | Validity end | Internal | ≥ valid_from |
| `is_primary` | `boolean` | No | | Primary identifier | Internal | one primary per type |

Indexes: `UNIQUE(identifier_type, identifier_value) WHERE is_deleted=false`; `(beneficiary_id)`.

### 2.3 `contact`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `contact_id` | `uuid` | No | PK | | Internal | |
| `beneficiary_id` | `uuid` | No | FK | Owner | Internal | FK |
| `contact_type` | `varchar(20)` | No | | Kind | PII | enum: Phone/Email/Address/EmergencyContact |
| `value` | `varchar(256)` | No | | Contact value | PII | type-specific (email/phone regex) |
| `preferred_channel` | `varchar(10)` | Yes | | Routing hint | Internal | enum: SMS/Email/Push |
| `is_primary` | `boolean` | No | | Primary flag | Internal | |

### 2.4 `family_group` / `dependent_link`

| Table.Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `family_group.family_group_id` | `uuid` | No | PK | | Internal |
| `family_group.family_code` | `varchar(20)` | No | UK | Family business code | PII |
| `family_group.head_beneficiary_id` | `uuid` | Yes | logical FK | Head of family | Internal |
| `dependent_link.dependent_link_id` | `uuid` | No | PK | | Internal |
| `dependent_link.family_group_id` | `uuid` | No | FK | | Internal |
| `dependent_link.guardian_beneficiary_id` | `uuid` | No | FK | Guardian | Internal |
| `dependent_link.dependent_beneficiary_id` | `uuid` | No | logical FK | Dependent | Internal |
| `dependent_link.relationship` | `varchar(16)` | No | | Relationship | PII (enum: Child/Spouse/Parent/Other) |

### 2.5 `beneficiary_history`

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `history_id` | `uuid` | No | PK | | Internal |
| `beneficiary_id` | `uuid` | No | FK | Subject | Internal |
| `row_version` | `integer` | No | | Version captured | Internal |
| `snapshot` | `jsonb` | No | | Full row snapshot | PII/PHI |
| `change_type` | `varchar(12)` | No | | INSERT/UPDATE/SOFT_DELETE | Internal |
| `valid_from` | `timestamptz` | No | | System-time start | Internal |
| `valid_to` | `timestamptz` | Yes | | System-time end (null=current) | Internal |
| `changed_by` | `uuid` | No | | Actor | Internal |

> The same `_history` shape (history_id, {entity}_id, row_version, snapshot, change_type, valid_from, valid_to, changed_by) applies to every history table: `investigation_order_history`, `prescription_history`, `authorization_history`, `coverage_history`, `encounter_history`.

---

## 3. Domain: Policy & Coverage (`policy` schema)

### 3.1 `policy`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `policy_id` | `uuid` | No | PK | | Internal | |
| `policy_no` | `varchar(30)` | No | UK | Business key | Internal | |
| `sponsor` | `varchar(120)` | No | | Program/donor | Internal | |
| `effective_from` | `date` | No | | Start | Internal | |
| `effective_to` | `date` | Yes | | End | Internal | ≥ from |
| `status` | `varchar(16)` | No | | | Internal | enum: Active/Suspended/Expired |

### 3.2 `coverage`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `coverage_id` | `uuid` | No | PK | | Internal | |
| `policy_id` | `uuid` | No | FK | Parent policy | Internal | FK |
| `beneficiary_id` | `uuid` | No | logical FK | Covered person | Internal | validated via event |
| `benefit_category_id` | `uuid` | No | FK | Category | Internal | FK |
| `effective_from` | `date` | No | | | Internal | |
| `effective_to` | `date` | Yes | | | Internal | |
| `status` | `varchar(16)` | No | | | Internal | enum |

### 3.3 `coverage_limit`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `coverage_limit_id` | `uuid` | No | PK | | Internal | |
| `coverage_id` | `uuid` | No | FK | | Internal | FK |
| `limit_type` | `varchar(16)` | No | | | Internal | enum: Annual/PerEncounter/Lifetime/Count |
| `limit_value` | `numeric(14,2)` | No | | Ceiling | Internal | ≥ 0 |
| `consumed_value` | `numeric(14,2)` | No | | Accumulator | Internal | 0 ≤ consumed ≤ limit |
| `currency_code` | `char(3)` | Yes | | For monetary limits | Internal | ISO 4217 |
| `reset_period` | `varchar(12)` | No | | | Internal | enum: None/Monthly/Quarterly/Yearly |

> `consumed_value` is updated transactionally on consume/dispense events; `CHECK (consumed_value <= limit_value)`.

### 3.4 `benefit_category`

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `benefit_category_id` | `uuid` | No | PK | | Public |
| `code` | `varchar(20)` | No | UK | LAB/IMAGING/PHARMACY/CONSULT/REFERRAL | Public |
| `name` | `varchar(80)` | No | | Display | Public |

---

## 4. Domain: Eligibility (`eligibility` schema)

### 4.1 `eligibility_snapshot`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `snapshot_id` | `uuid` | No | PK | | Internal | |
| `beneficiary_id` | `uuid` | No | logical FK | | Internal | |
| `coverage_id` | `uuid` | No | FK/logical | | Internal | |
| `decision` | `varchar(20)` | No | | | Internal | enum: Eligible/Ineligible/NeedsAuthorization |
| `limit_state` | `jsonb` | Yes | | Denormalized limits | Internal | |
| `computed_at` | `timestamptz` | No | | | Internal | |
| `expires_at` | `timestamptz` | No | | Cache TTL bound | Internal | > computed_at |
| `version_hash` | `varchar(64)` | No | | Staleness guard | Internal | |

Index: `(beneficiary_id, coverage_id) INCLUDE (decision, expires_at)`.

---

## 5. Domain: Provider (`provider` schema)

### 5.1 `provider`

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `provider_id` | `uuid` | No | PK | | Internal |
| `provider_code` | `varchar(20)` | No | UK | Business code | Internal |
| `legal_name` | `varchar(160)` | No | | | Internal |
| `provider_type` | `varchar(16)` | No | | enum: Hospital/Clinic/Lab/Pharmacy/Imaging | Internal |
| `status` | `varchar(16)` | No | | enum: Active/Suspended/Terminated | Internal |

### 5.2 `provider_location`

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `location_id` | `uuid` | No | PK | | Internal |
| `provider_id` | `uuid` | No | FK | | Internal |
| `name` | `varchar(120)` | No | | | Internal |
| `governorate` | `varchar(60)` | Yes | | Egyptian governorate | Internal |
| `address` | `varchar(256)` | Yes | | | Internal |
| `geo_point` | `geography(Point)` | Yes | | Lat/long | Internal |
| `is_primary` | `boolean` | No | | | Internal |

### 5.3 `provider_contract` / `contract_service_line`

| Table.Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `provider_contract.contract_id` | `uuid` | No | PK | | Internal |
| `provider_contract.provider_id` | `uuid` | No | FK | | Internal |
| `provider_contract.contract_no` | `varchar(30)` | No | UK | | Internal |
| `provider_contract.effective_from` | `date` | No | | | Internal |
| `provider_contract.effective_to` | `date` | Yes | | | Internal |
| `provider_contract.status` | `varchar(16)` | No | | enum | Internal |
| `contract_service_line.service_line_id` | `uuid` | No | PK | | Internal |
| `contract_service_line.contract_id` | `uuid` | No | FK | | Internal |
| `contract_service_line.service_type` | `varchar(16)` | No | enum: Lab/Imaging/Consult/Procedure | Internal |
| `contract_service_line.code_system` | `varchar(10)` | No | enum: CPT/LOINC/LOCAL | Internal |
| `contract_service_line.code` | `varchar(20)` | No | | Internal |
| `contract_service_line.agreed_price` | `numeric(14,2)` | No | ≥ 0 | Internal |
| `contract_service_line.currency_code` | `char(3)` | No | ISO 4217 | Internal |

---

## 6. Domain: EMR / Clinical (`emr` schema)

### 6.1 `appointment`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `appointment_id` | `uuid` | No | PK | | Internal | |
| `beneficiary_id` | `uuid` | No | logical FK | | PHI | |
| `provider_id` | `uuid` | No | logical FK | | Internal | |
| `location_id` | `uuid` | Yes | logical FK | | Internal | |
| `scheduled_start` | `timestamptz` | No | | | PHI | |
| `scheduled_end` | `timestamptz` | Yes | | | PHI | ≥ start |
| `status` | `varchar(16)` | No | | | Internal | enum: Booked/CheckedIn/Completed/NoShow/Cancelled |

### 6.2 `encounter`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `encounter_id` | `uuid` | No | PK | | Internal | |
| `encounter_no` | `varchar(20)` | No | UK | `ENC-YYYY-NNNNNN` | Internal | regex |
| `beneficiary_id` | `uuid` | No | logical FK | | PHI | |
| `appointment_id` | `uuid` | Yes | FK | | Internal | |
| `provider_id` | `uuid` | No | logical FK | | Internal | |
| `encounter_class` | `varchar(16)` | No | | | PHI | enum: Ambulatory/Emergency/Inpatient/Virtual |
| `started_at` | `timestamptz` | No | | | PHI | |
| `ended_at` | `timestamptz` | Yes | | | PHI | ≥ started |
| `status` | `varchar(16)` | No | | | Internal | enum: InProgress/Finished/Cancelled |

Index: `(beneficiary_id, started_at DESC)`.

### 6.3 `emr_note` (SOAP)

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `note_id` | `uuid` | No | PK | | Internal |
| `encounter_id` | `uuid` | No | FK | | Internal |
| `note_type` | `varchar(12)` | No | | enum: SOAP/Progress/Nursing | Internal |
| `subjective` | `text` | Yes | | S | **PHI** |
| `objective` | `text` | Yes | | O | **PHI** |
| `assessment` | `text` | Yes | | A | **PHI** |
| `plan` | `text` | Yes | | P | **PHI** |
| `authored_by` | `uuid` | No | | Author | Internal |
| `authored_at` | `timestamptz` | No | | | Internal |
| `is_signed` | `boolean` | No | | Locked when signed | Internal |

### 6.4 `diagnosis`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `diagnosis_id` | `uuid` | No | PK | | Internal | |
| `encounter_id` | `uuid` | No | FK | | Internal | |
| `icd_code` | `varchar(10)` | No | logical FK | ICD-10 code | **PHI** | exists in masterdata.icd_code |
| `diagnosis_rank` | `varchar(10)` | No | | | PHI | enum: Primary/Secondary |
| `clinical_status` | `varchar(12)` | No | | | PHI | enum: Active/Resolved/Recurrence |
| `recorded_at` | `timestamptz` | No | | | Internal | |

### 6.5 `vital`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `vital_id` | `uuid` | No | PK | | Internal | |
| `encounter_id` | `uuid` | No | FK | | Internal | |
| `vital_type` | `varchar(10)` | No | | enum: BP/HR/Temp/SpO2/Weight/Height/BMI | PHI | |
| `value_num` | `numeric(10,3)` | Yes | | | **PHI** | range per type |
| `unit` | `varchar(10)` | Yes | | | PHI | |
| `loinc_code` | `varchar(10)` | Yes | logical FK | LOINC-ready | PHI | |
| `measured_at` | `timestamptz` | No | | | Internal | |

### 6.6 `allergy`

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `allergy_id` | `uuid` | No | PK | | Internal |
| `beneficiary_id` | `uuid` | No | logical FK | | **PHI** |
| `allergen_id` | `uuid` | No | FK | masterdata.allergen | PHI |
| `reaction` | `varchar(120)` | Yes | | | PHI |
| `severity` | `varchar(10)` | No | | enum: Mild/Moderate/Severe | PHI |
| `status` | `varchar(10)` | No | | enum: Active/Inactive/Resolved | PHI |

### 6.7 `medication_history`

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `med_history_id` | `uuid` | No | PK | | Internal |
| `beneficiary_id` | `uuid` | No | logical FK | | **PHI** |
| `drug_id` | `uuid` | No | FK | masterdata.drug | PHI |
| `source` | `varchar(12)` | No | | enum: Prescribed/SelfReported/External | Internal |
| `start_date` | `date` | Yes | | | PHI |
| `end_date` | `date` | Yes | | | PHI |
| `status` | `varchar(10)` | No | | enum: Active/Stopped | PHI |

---

## 7. Domain: Investigation Orders (`orders` schema)

### 7.1 `investigation_order`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `order_id` | `uuid` | No | PK | | Internal | |
| `order_no` | `varchar(20)` | No | UK | `ORD-YYYY-NNNNNN` | Internal | regex |
| `beneficiary_id` | `uuid` | No | logical FK | | PHI | |
| `encounter_id` | `uuid` | No | logical FK | | Internal | |
| `ordering_provider_id` | `uuid` | No | logical FK | | Internal | |
| `authorization_id` | `uuid` | Yes | logical FK | If auth required | Internal | |
| `order_type` | `varchar(12)` | No | | enum: Lab/Imaging/Procedure | Internal | |
| `status` | `varchar(16)` | No | | Lifecycle | Internal | enum (see §11) |
| `requested_at` | `timestamptz` | No | | | Internal | |
| `expires_at` | `timestamptz` | Yes | | Validity window | Internal | > requested |

Indexes: `UNIQUE(order_no)`; `(beneficiary_id, status)`; partial `(expires_at) WHERE status IN ('Active','PartiallyUsed')`.

### 7.2 `order_line`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `order_line_id` | `uuid` | No | PK | | Internal | |
| `order_id` | `uuid` | No | FK | | Internal | FK |
| `code_system` | `varchar(10)` | No | | enum: CPT/LOINC/LOCAL | Internal | |
| `code` | `varchar(20)` | No | | | Internal | exists in masterdata |
| `description` | `varchar(200)` | Yes | | | Internal | |
| `quantity_ordered` | `numeric(14,3)` | No | | | Internal | > 0 |
| `quantity_consumed` | `numeric(14,3)` | No | | Accumulator | Internal | `CHECK (0 ≤ consumed ≤ ordered)` |
| `status` | `varchar(16)` | No | | | Internal | enum: Active/PartiallyUsed/Completed/Cancelled |

### 7.3 `order_fulfillment` (append-only)

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `fulfillment_id` | `uuid` | No | PK | | Internal | |
| `order_line_id` | `uuid` | No | FK | | Internal | FK |
| `performing_provider_id` | `uuid` | No | logical FK | | Internal | |
| `quantity` | `numeric(14,3)` | No | | Consumed amount | Internal | > 0 |
| `idempotency_key` | `varchar(80)` | No | UK | Dedup guarantee | Internal | `UNIQUE` |
| `result_document_id` | `uuid` | Yes | logical FK | Result blob ref | PHI | |
| `consumed_at` | `timestamptz` | No | | | Internal | |
| `consumed_by` | `uuid` | No | | Actor | Internal | |

> No `updated_at`/soft-delete: rows are immutable (no reuse). Full history via `audit_event`.

---

## 8. Domain: Pharmacy (`pharmacy` schema)

### 8.1 `prescription`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `prescription_id` | `uuid` | No | PK | | Internal | |
| `rx_no` | `varchar(20)` | No | UK | `RX-YYYY-NNNNNN` | Internal | regex |
| `beneficiary_id` | `uuid` | No | logical FK | | PHI | |
| `encounter_id` | `uuid` | No | logical FK | | Internal | |
| `prescriber_id` | `uuid` | No | logical FK | | Internal | |
| `authorization_id` | `uuid` | Yes | logical FK | | Internal | |
| `status` | `varchar(20)` | No | | Lifecycle | Internal | enum (see §11) |
| `submitted_at` | `timestamptz` | Yes | | | Internal | |
| `expires_at` | `timestamptz` | Yes | | | Internal | |

### 8.2 `prescription_line`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `prescription_line_id` | `uuid` | No | PK | | Internal | |
| `prescription_id` | `uuid` | No | FK | | Internal | FK |
| `drug_id` | `uuid` | No | FK | masterdata.drug | PHI | |
| `dose` | `varchar(40)` | Yes | | | PHI | |
| `route` | `varchar(30)` | Yes | | | PHI | |
| `frequency` | `varchar(40)` | Yes | | | PHI | |
| `quantity_prescribed` | `numeric(14,3)` | No | | | Internal | > 0 |
| `quantity_dispensed` | `numeric(14,3)` | No | | Accumulator | Internal | `CHECK (0 ≤ dispensed ≤ prescribed)` |
| `refills_allowed` | `integer` | No | | | Internal | ≥ 0 |
| `status` | `varchar(20)` | No | | | Internal | enum: Active/PartiallyDispensed/Dispensed/Cancelled |

### 8.3 `dispense_event` (append-only)

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `dispense_id` | `uuid` | No | PK | | Internal | |
| `prescription_line_id` | `uuid` | No | FK | | Internal | FK |
| `dispensing_pharmacy_id` | `uuid` | No | logical FK | | Internal | |
| `quantity` | `numeric(14,3)` | No | | | Internal | > 0 |
| `idempotency_key` | `varchar(80)` | No | UK | | Internal | `UNIQUE` |
| `batch_no` | `varchar(40)` | Yes | | Lot/batch | Internal | |
| `dispensed_at` | `timestamptz` | No | | | Internal | |
| `dispensed_by` | `uuid` | No | | Actor | Internal | |

---

## 9. Domain: Approvals & Referrals (`approvals` schema)

### 9.1 `authorization`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `authorization_id` | `uuid` | No | PK | | Internal | |
| `auth_no` | `varchar(20)` | No | UK | `AUTH-YYYY-NNNNNN` | Internal | regex |
| `beneficiary_id` | `uuid` | No | logical FK | | PHI | |
| `requested_for` | `varchar(12)` | No | | enum: Order/Prescription/Referral | Internal | |
| `subject_ref` | `uuid` | No | | Polymorphic target | Internal | |
| `status` | `varchar(16)` | No | | Lifecycle | Internal | enum (see §11) |
| `requested_at` | `timestamptz` | No | | | Internal | |
| `decided_at` | `timestamptz` | Yes | | | Internal | |
| `expires_at` | `timestamptz` | Yes | | | Internal | |

### 9.2 `authorization_decision` (append-only)

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `decision_id` | `uuid` | No | PK | | Internal |
| `authorization_id` | `uuid` | No | FK | | Internal |
| `decision` | `varchar(20)` | No | | enum: Approve/PartiallyApprove/Reject/RequestInfo/Override/EmergencyApprove | Internal |
| `rationale` | `text` | Yes | | | PHI |
| `decided_by` | `uuid` | No | | Approver | Internal |
| `decided_at` | `timestamptz` | No | | | Internal |
| `applied_limits` | `jsonb` | Yes | | Limits applied | Internal |

### 9.3 `referral` / `referral_event`

| Table.Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `referral.referral_id` | `uuid` | No | PK | | Internal |
| `referral.referral_no` | `varchar(20)` | No | UK | `REF-YYYY-NNNNNN` | Internal |
| `referral.beneficiary_id` | `uuid` | No | logical FK | | PHI |
| `referral.from_provider_id` | `uuid` | No | logical FK | | Internal |
| `referral.to_provider_id` | `uuid` | No | logical FK | | Internal |
| `referral.specialty` | `varchar(60)` | No | | | PHI |
| `referral.status` | `varchar(16)` | No | | enum (see §11) | Internal |
| `referral.requested_at` | `timestamptz` | No | | | Internal |
| `referral_event.referral_event_id` | `uuid` | No | PK | | Internal |
| `referral_event.referral_id` | `uuid` | No | FK | | Internal |
| `referral_event.event_type` | `varchar(30)` | No | | | Internal |
| `referral_event.payload` | `jsonb` | Yes | | | PHI |
| `referral_event.occurred_at` | `timestamptz` | No | | | Internal |

---

## 10. Domain: Identity, Documents, Notifications, Audit, Master Data

### 10.1 `identity` schema (abridged)

| Table.Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `app_user.user_id` | `uuid` | No | PK | | Internal |
| `app_user.external_oid` | `varchar(64)` | No | UK | Keycloak subject id | PII |
| `app_user.email` | `varchar(256)` | No | | | PII |
| `app_user.provider_id` | `uuid` | Yes | logical FK | Provider scope | Internal |
| `app_user.status` | `varchar(10)` | No | enum: Active/Disabled | Internal |
| `role.role_id` / `role.code` | `uuid`/`varchar(30)` | No | PK/UK | Physician/Pharmacist/CaseWorker/Approver/Admin/Auditor | Internal |
| `permission.permission_id` / `code` | `uuid`/`varchar(40)` | No | PK/UK | e.g. `orders:consume` | Internal |
| `user_role.*` | | | | user↔role, `scope_provider_id` | Internal |
| `role_permission.*` | | | | role↔permission | Internal |

### 10.2 `document` schema

| Table.Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `document.document_id` | `uuid` | No | PK | | Internal |
| `document.doc_type` | `varchar(20)` | No | | enum: LabResult/ImagingReport/Consent/IDScan/Referral | Internal |
| `document.owner_beneficiary_id` | `uuid` | No | logical FK | | PHI |
| `document.classification` | `varchar(10)` | No | | PHI/PII/Internal | Internal |
| `document.blob_container` | `varchar(60)` | No | | | Internal |
| `document.current_version_no` | `varchar(10)` | No | | | Internal |
| `document_version.version_id` | `uuid` | No | PK | | Internal |
| `document_version.document_id` | `uuid` | No | FK | | Internal |
| `document_version.blob_path` | `varchar(256)` | No | | | Internal |
| `document_version.checksum_sha256` | `char(64)` | No | | Integrity | Internal |
| `document_version.size_bytes` | `bigint` | No | | | Internal |

### 10.3 `notification` schema

| Table.Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `notification.notification_id` | `uuid` | No | PK | | Internal |
| `notification.template_id` | `uuid` | No | FK | | Internal |
| `notification.recipient_beneficiary_id` | `uuid` | Yes | logical FK | | PII |
| `notification.channel` | `varchar(8)` | No | enum: SMS/Email/Push/InApp | Internal |
| `notification.status` | `varchar(10)` | No | enum: Queued/Sent/Delivered/Failed | Internal |
| `notification.payload` | `jsonb` | Yes | | Rendered vars (minimized) | PII |
| `notification_template.code/channel/locale/body_template` | | | | localized (ar/en) | Internal |
| `delivery_attempt.*` | | | | attempt_no, result, provider_response | Internal |

### 10.4 `audit.audit_event` (append-only, partitioned monthly)

| Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `audit_event_id` | `uuid` | No | PK | | Internal |
| `service_name` | `varchar(40)` | No | | Producer | Internal |
| `entity_type` | `varchar(40)` | No | | | Internal |
| `entity_id` | `uuid` | No | | | Internal |
| `action` | `varchar(20)` | No | | enum: CREATE/UPDATE/SOFT_DELETE/STATE_CHANGE/CONSUME/DISPENSE/DECISION | Internal |
| `actor_user_id` | `uuid` | Yes | | | Internal |
| `before_state` | `jsonb` | Yes | | Minimized snapshot | PHI/PII |
| `after_state` | `jsonb` | Yes | | Minimized snapshot | PHI/PII |
| `correlation_id` | `varchar(64)` | No | | Cross-service trace | Internal |
| `occurred_at` | `timestamptz` | No | | | Internal |

Index: `(entity_type, entity_id, occurred_at)`; `(correlation_id)`.

### 10.5 `masterdata` schema (reference — Public)

| Table | Key columns | Notes |
|---|---|---|
| `icd_code` | `code` PK, `title`, `chapter`, `is_billable`, `icd11_map` | ICD-10 now, ICD-11 ready |
| `cpt_code` | `code` PK, `description`, `category` | procedures |
| `loinc_code` | `code` PK, `long_name`, `component`, `property` | labs (LOINC-ready) |
| `atc_class` | `atc_code` PK, `title`, `level` | drug classification |
| `drug` | `drug_id` PK, `drug_code` UK, `name`, `atc_code` FK, `form`, `strength` | Drug Master |
| `drug_interaction` | `interaction_id` PK, `drug_a_id`, `drug_b_id`, `severity`, `description` | enum severity: Minor/Moderate/Major/Contraindicated |
| `allergen` | `allergen_id` PK, `code` UK, `name`, `category` | Allergy DB |

---

## 11. Enumerations (Canonical)

### 11.1 Lifecycle statuses (see [23-state-machines.md](23-state-machines.md))

| Enum | Values |
|---|---|
| **Beneficiary/Member status** | Pending, Active, Suspended, Expired, Blocked, Inactive |
| **Investigation Order status** | Requested, PendingApproval, Approved, Rejected, Active, PartiallyUsed, Completed, Expired, Cancelled |
| **Order Line status** | Active, PartiallyUsed, Completed, Cancelled |
| **Prescription status** | Draft, Submitted, Approved, Rejected, PartiallyDispensed, Dispensed, Expired, Cancelled |
| **Prescription Line status** | Active, PartiallyDispensed, Dispensed, Cancelled |
| **Authorization status** | Draft, Submitted, UnderReview, Approved, PartiallyApproved, Rejected, InfoRequested, Overridden, EmergencyApproved, Expired |
| **Referral status** | Requested, Accepted, Scheduled, Completed, Rejected, Cancelled, Expired |
| **Encounter status** | InProgress, Finished, Cancelled |
| **Appointment status** | Booked, CheckedIn, Completed, NoShow, Cancelled |
| **Policy/Coverage status** | Active, Suspended, Expired |
| **Provider status** | Active, Suspended, Terminated |

### 11.2 Identifier types

`NationalID`, `Passport`, `RefugeeID`, `UNHCRNo`, `MemberNo`

### 11.3 Order / code systems

Order types: `Lab`, `Imaging`, `Procedure`. Code systems: `CPT`, `LOINC`, `LOCAL`.

### 11.4 Other enums

| Enum | Values |
|---|---|
| Sex | male, female, other, unknown |
| Contact type | Phone, Email, Address, EmergencyContact |
| Benefit category | LAB, IMAGING, PHARMACY, CONSULT, REFERRAL |
| Limit type | Annual, PerEncounter, Lifetime, Count |
| Reset period | None, Monthly, Quarterly, Yearly |
| Eligibility decision | Eligible, Ineligible, NeedsAuthorization |
| Note type | SOAP, Progress, Nursing |
| Vital type | BP, HR, Temp, SpO2, Weight, Height, BMI |
| Allergy severity | Mild, Moderate, Severe |
| Drug interaction severity | Minor, Moderate, Major, Contraindicated |
| Channel | SMS, Email, Push, InApp |
| Roles | Physician, Pharmacist, CaseWorker, Approver, Admin, Auditor |

---

## 12. Reference / Lookup Tables Summary

| Lookup | Owner schema | Distribution |
|---|---|---|
| `benefit_category` | policy | replicated read |
| `icd_code`, `cpt_code`, `loinc_code` | masterdata | cache + OpenSearch |
| `drug`, `atc_class`, `drug_interaction`, `allergen` | masterdata | cache + OpenSearch |
| `notification_template` | notification | local |
| `role`, `permission` | identity | local |

---

## 13. Cross-References

- Structural model & indexes: [15-database-erd.md](15-database-erd.md)
- Transitions/guards for status enums: [23-state-machines.md](23-state-machines.md)
- Sensitivity handling, RLS, masking, minimization: [18-security-model.md](18-security-model.md)
- API field shapes: [17-api-specifications.md](17-api-specifications.md)
