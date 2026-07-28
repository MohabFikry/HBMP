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

## 10A. Domain: Claims (`claims` schema)

Added in **Phase 10b** — authoritative module design: [36-claims-management.md](36-claims-management.md). Numbered `10A` so the existing `§11` enumeration references stay valid.

> **Minimum-necessary note (hard rule):** the `claims` schema contains **no diagnosis, ICD code, EMR note, lab/imaging result value, or other clinical column anywhere** — adjudication is on *service codes and amounts only* ([36 §2.2](36-claims-management.md), [11 §3.2](11-permission-matrix.md)). Claims rows are **Internal/financial**; `beneficiary_id` is a **PHI-linking** identifier (it associates money with a person, so it is treated as PHI for RLS, masking, and audit-on-read) and clinical narrative is stripped server-side from every claims projection. Where medical necessity must be judged, the line is routed to a clinical reviewer in `approvals`; the clinical opinion lives there, not here.

### 10A.1 `claim`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `claim_id` | `uuid` | No | PK | UUID v7 | Internal | v7 generated |
| `claim_no` | `varchar(20)` | No | UK | Business key `CLM-YYYY-NNNNNN` | Internal | regex `^CLM-\d{4}-\d{6}$` |
| `origin` | `varchar(20)` | No | | Origination channel | Internal | enum (see §11.5) |
| `beneficiary_id` | `uuid` | No | logical FK | Claim subject | **PHI (link)** | validated via event |
| `provider_id` | `uuid` | Yes | logical FK | Payee provider; **null for reimbursement** | Internal | required when `origin <> 'Reimbursement'` |
| `provider_location_id` | `uuid` | Yes | logical FK | Branch that rendered the service | Internal | must belong to `provider_id` |
| `batch_id` | `uuid` | Yes | FK | Owning batch | Internal | FK `claim_batch`; ≤ 1 open batch |
| `authorization_id` | `uuid` | Yes | logical FK | Pre-auth linkage | Internal | mandatory for gated services |
| `service_date_from` | `date` | No | | Service period start | Internal | ≤ today |
| `service_date_to` | `date` | Yes | | Service period end | Internal | ≥ `service_date_from` |
| `currency_code` | `char(3)` | No | | Claim currency | Internal | ISO 4217 |
| `claimed_amount` | `numeric(14,2)` | No | | As billed/submitted | Internal | ≥ 0 |
| `priced_amount` | `numeric(14,2)` | Yes | | Repriced to contract tariff | Internal | ≥ 0 |
| `approved_amount` | `numeric(14,2)` | Yes | | Sum of approved line amounts | Internal | 0 ≤ approved ≤ priced |
| `adjusted_amount` | `numeric(14,2)` | Yes | | Net of adjustments (**signed**) | Internal | may be negative |
| `net_payable` | `numeric(14,2)` | Yes | | `approved + adjusted` | Internal | ≥ 0 unless dual-control approved |
| `status` | `varchar(20)` | No | | Lifecycle status | Internal | enum (see §11) |
| `submitted_at` | `timestamptz` | Yes | | Submission (UTC) | Internal | |
| `decided_at` | `timestamptz` | Yes | | Final decision (UTC) | Internal | ≥ `submitted_at` |
| `row_version` | `integer` | No | | Optimistic concurrency (ETag) | Internal | |

Indexes: PK; `UNIQUE(claim_no)`; `(beneficiary_id, status)`; `(provider_id, service_date_from)`; `(batch_id)`; `(status)`.

> Submitted claims are **never mutated or hard-deleted**: corrections are `claim_adjustment` rows or a compensating `Void` + re-claim ([36 §2.5](36-claims-management.md)).

### 10A.2 `claim_line`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `claim_line_id` | `uuid` | No | PK | | Internal | |
| `claim_id` | `uuid` | No | FK | Parent claim | Internal | FK `claim` |
| `fulfillment_ref` | `uuid` | Yes | logical FK | `orders.order_fulfillment.fulfillment_id` **or** `pharmacy.dispense_event.dispense_id` | Internal | required for any payable line |
| `fulfillment_type` | `varchar(20)` | No | | Discriminator for `fulfillment_ref` | Internal | enum (see §11.5); `None` ⇒ `fulfillment_ref` null |
| `code_system` | `varchar(10)` | No | | Coding system | Internal | enum: CPT/LOINC/LOCAL/DRUG |
| `code` | `varchar(20)` | No | | Service/drug code | Internal | exists in masterdata |
| `description` | `varchar(200)` | Yes | | Display text (non-clinical) | Internal | |
| `quantity` | `numeric(14,3)` | No | | Billed quantity | Internal | > 0 |
| `billed_amount` | `numeric(14,2)` | No | | As billed by provider/receipt | Internal | ≥ 0 |
| `contract_price` | `numeric(14,2)` | Yes | | `contract_service_line.agreed_price` on service date | Internal | ≥ 0; null ⇒ `NO_TARIFF` → manual pricing |
| `allowed_amount` | `numeric(14,2)` | Yes | | Payable after adjudication | Internal | 0 ≤ allowed ≤ max(billed, contract_price) |
| `member_share` | `numeric(14,2)` | Yes | | Co-pay / deductible portion | Internal | ≥ 0 |
| `status` | `varchar(20)` | No | | Line lifecycle | Internal | enum (see §11) |
| `system_recommendation` | `varchar(24)` | Yes | | Pre-adjudication output | Internal | enum (see §11.5) |
| `rule_version` | `varchar(20)` | Yes | | Rule-set version applied | Internal | semver |

Indexes: PK; `(claim_id)`; `(status)`; `(code_system, code)`;
**`UNIQUE(fulfillment_ref) WHERE fulfillment_ref IS NOT NULL AND status <> 'Void'`** — the **no-double-billing guard**: at most one live payable line per fulfillment/dispense record. Violations surface as `DUPLICATE_CLAIM`.

### 10A.3 `claim_decision` (append-only)

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `decision_id` | `uuid` | No | PK | | Internal | |
| `claim_line_id` | `uuid` | No | FK | Line decided | Internal | FK `claim_line` |
| `decision` | `varchar(24)` | No | | Officer decision | Internal | enum (see §11.5) |
| `allowed_amount` | `numeric(14,2)` | Yes | | Amount allowed by this decision | Internal | ≥ 0; required for Approve/PartiallyApprove |
| `reason_codes` | `text[]` | Yes | | Coded reasons (all applicable) | Internal | values from §11.5; **mandatory** for Deny/PartiallyApprove |
| `rationale` | `text` | Yes | | Free-text justification (non-clinical) | Internal | **mandatory** for deny/adjust/override |
| `decided_by` | `uuid` | No | | Claims Officer | Internal | **SoD:** ≠ originator; not provider-affiliated |
| `decided_at` | `timestamptz` | No | | Decision time (UTC) | Internal | |
| `rule_version` | `varchar(20)` | Yes | | Rule-set version at decision | Internal | semver |
| `correlation_id` | `varchar(64)` | No | | Cross-service trace | Internal | |

Indexes: `(claim_line_id, decided_at)`; `(decided_by)`; `(correlation_id)`.

> No `updated_at`/soft-delete: rows are **immutable**. A changed outcome is a *new* decision row; full history via `audit_event`.

### 10A.4 `claim_adjustment` (append-only)

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `adjustment_id` | `uuid` | No | PK | | Internal | |
| `claim_line_id` | `uuid` | No | FK | Line adjusted | Internal | FK `claim_line` |
| `adjustment_type` | `varchar(20)` | No | | Kind of adjustment | Internal | enum (see §11.5) |
| `amount_delta` | `numeric(14,2)` | No | | **Signed** delta (debit −/credit +) | Internal | ≠ 0; nets into batch rollup |
| `reason_code` | `varchar(40)` | No | | Coded reason | Internal | values from §11.5 |
| `rationale` | `text` | No | | Mandatory justification | Internal | non-empty |
| `recovers_claim_line_id` | `uuid` | Yes | FK | Original line recovered against | Internal | **required** for Recovery/Clawback |
| `created_by` | `uuid` | No | | Actor | Internal | dual control above value threshold |
| `created_at` | `timestamptz` | No | | UTC | Internal | |

Indexes: `(claim_line_id, created_at)`; `(adjustment_type)`; `(recovers_claim_line_id)`.

### 10A.5 `claim_batch`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `batch_id` | `uuid` | No | PK | | Internal | |
| `batch_no` | `varchar(20)` | No | UK | Business key `BAT-YYYY-NNNNNN` | Internal | regex `^BAT-\d{4}-\d{6}$` |
| `batch_type` | `varchar(16)` | No | | Provider settlement vs reimbursement cohort | Internal | enum: Provider/Reimbursement |
| `selection_mode` | `varchar(16)` | No | | How claims were selected | Internal | enum (see §11.5) |
| `payee_provider_id` | `uuid` | Yes | logical FK | Payee (null for reimbursement batches) | Internal | required when `batch_type='Provider'` |
| `provider_location_id` | `uuid` | Yes | logical FK | Branch, when branch-level settlement | Internal | must belong to payee |
| `period_from` | `date` | No | | Period start | Internal | |
| `period_to` | `date` | No | | Period end | Internal | ≥ `period_from` |
| `status` | `varchar(20)` | No | | Batch lifecycle | Internal | enum (see §11) |
| `total_claimed` | `numeric(16,2)` | No | | Rollup: as billed | Internal | ≥ 0 |
| `total_priced` | `numeric(16,2)` | No | | Rollup: repriced | Internal | ≥ 0 |
| `total_approved` | `numeric(16,2)` | No | | Rollup: approved | Internal | ≥ 0 |
| `total_adjusted` | `numeric(16,2)` | No | | Rollup: adjustments (**signed**) | Internal | may be negative |
| `total_denied` | `numeric(16,2)` | No | | Rollup: denied value | Internal | ≥ 0 |
| `net_payable` | `numeric(16,2)` | No | | `total_approved + total_adjusted` | Internal | ≥ 0 unless dual-control approved |
| `created_by` | `uuid` | No | | Batch creator | Internal | **SoD:** creator ≠ settlement releaser |
| `decided_at` | `timestamptz` | Yes | | All lines decided (UTC) | Internal | |
| `settlement_document_id` | `uuid` | Yes | logical FK | Settlement advice in `document` (WORM) | Internal | set at `SettlementIssued` |

Indexes: `UNIQUE(batch_no)`; `(payee_provider_id, period_from)`; `(status)`; and on `claim`: **`UNIQUE(claim_id) WHERE batch_id IS NOT NULL AND batch_status IN ('Open','UnderReview')`** — a claim sits in **at most one open batch**.

> Rollup totals are recomputed on every line decision/adjustment and **frozen at `SettlementIssued`**.

### 10A.6 `reimbursement_request`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `request_id` | `uuid` | No | PK | | Internal | |
| `claim_id` | `uuid` | Yes | FK | Claim raised from this request | Internal | FK `claim`; set on match |
| `beneficiary_id` | `uuid` | No | logical FK | Claimant | **PHI (link)** | |
| `submitted_by` | `uuid` | No | | Member / Reception / Case Manager | Internal | |
| `submitted_at` | `timestamptz` | No | | UTC | Internal | |
| `receipt_total` | `numeric(14,2)` | No | | Total on receipt(s) | Internal | ≥ 0 |
| `currency_code` | `char(3)` | No | | | Internal | ISO 4217 |
| `status` | `varchar(20)` | No | | Reimbursement lifecycle | Internal | enum (see §11) |
| `match_confidence` | `numeric(5,4)` | Yes | | Auto-match confidence 0–1 | Internal | below threshold ⇒ `ManualAssessment` |
| `match_method` | `varchar(12)` | No | | How the match was made | Internal | enum: AutoOcr/Manual/Unmatched |
| `linked_order_id` | `uuid` | Yes | logical FK | Authorized investigation order | Internal | one of order/prescription required |
| `linked_prescription_id` | `uuid` | Yes | logical FK | Authorized prescription | Internal | one of order/prescription required |

Indexes: `(beneficiary_id, submitted_at DESC)`; `(status)`; `(claim_id)`.

> No bank/payout details are stored here — payout happens through Mersal's existing finance process ([36 §3.3](36-claims-management.md)). Reimbursement is capped at **min(contract tariff, receipt)** unless an officer records an audited override.

### 10A.7 `claim_document` (link table)

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `claim_document_id` | `uuid` | No | PK | | Internal | |
| `claim_id` | `uuid` | Yes | FK | Linked claim | Internal | exactly one of claim/request set |
| `request_id` | `uuid` | Yes | FK | Linked reimbursement request | Internal | exactly one of claim/request set |
| `document_id` | `uuid` | No | logical FK | `document.document_id` (document-service) | Internal | scanned + encrypted |
| `doc_type` | `varchar(20)` | No | | Kind of evidence | Internal | enum (see §11.5) |
| `linked_by` | `uuid` | No | | Actor | Internal | |
| `linked_at` | `timestamptz` | No | | UTC | Internal | |

Indexes: `UNIQUE(claim_id, document_id)`; `UNIQUE(request_id, document_id)`; `(document_id)`.

> `ResultProof`/`DispenseProof` evidence proves a service **existed** (date + document reference) — claims roles never read the clinical **content** ([36 §9](36-claims-management.md)).

### 10A.8 `ocr_extraction`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `extraction_id` | `uuid` | No | PK | | Internal | |
| `document_id` | `uuid` | No | logical FK | Source document | Internal | |
| `field_name` | `varchar(40)` | No | | e.g. provider, date, amount, code | Internal | allow-list |
| `extracted_value` | `varchar(256)` | Yes | | Raw OCR value | Internal | |
| `confidence` | `numeric(5,4)` | No | | Engine confidence 0–1 | Internal | 0 ≤ c ≤ 1 |
| `page` | `integer` | Yes | | Source page | Internal | ≥ 1 |
| `region` | `jsonb` | Yes | | Bounding box for the overlay | Internal | |
| `accepted_by` | `uuid` | Yes | | Human who confirmed the value | Internal | |
| `accepted_at` | `timestamptz` | Yes | | Confirmation time (UTC) | Internal | |

Indexes: `(document_id, field_name)`; partial `(confidence) WHERE accepted_by IS NULL`.

> **OCR is assistive, never authoritative.** No extracted value affects money until `accepted_by`/`accepted_at` are set by a human; low confidence or any mismatch routes the request to `ManualAssessment`.

---

## 10B. Domain: Branch, Practitioner & Clinical Sensitivity (`provider`, `masterdata`, `orders` schemas)

Added in **Phase 14** — authoritative module design: [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md). Numbered `10B` so the existing `§11` enumeration references stay valid. `branch`, `user_branch_assignment`, `practitioner*` and `specialty` live in the **`provider`** schema (its remit widens from "contracted network" to "network & facilities"); `examination_type` is reference data in **`masterdata`**; `report_access_request` / `report_access_grant` live with the results they gate in **`orders`**.

> **Minimum-necessary note (hard rule):** branch scoping is an **additional narrowing filter, never a replacement** for the existing row/field rules ([11 §3](11-permission-matrix.md)) — a clinician still needs `TreatingRelationship` to open a record. For any result whose `sensitivity_level` ≠ `Standard`, the clinical **values and report documents are PHI with restricted disclosure**: non-authoring roles (including the medical approval team, case managers, and reporting) receive **existence metadata only** — category, date, status, ordering branch, and a `RESTRICTED` marker — never the values. Content is released only under a time-boxed `report_access_grant` or audited break-glass ([37 §6](37-branch-scoping-and-clinical-sensitivity.md), [18-security-model.md](18-security-model.md)).

### 10B.1 `branch`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `branch_id` | `uuid` | No | PK | UUID v7 | Internal | v7 generated |
| `branch_code` | `varchar(8)` | No | UK | Stable code used in business keys/reporting | Internal | uppercase `^[A-Z]{3,8}$` |
| `name_en` | `varchar(120)` | No | | English name | Internal | non-empty |
| `name_ar` | `varchar(120)` | No | | Arabic name (RTL display) | Internal | non-empty |
| `city` | `varchar(60)` | Yes | | City / governorate | Internal | |
| `address` | `varchar(256)` | Yes | | Street address | Internal | |
| `timezone` | `varchar(40)` | No | | IANA tz, default `'Africa/Cairo'` | Internal | valid IANA zone |
| `phone` | `varchar(30)` | Yes | | Branch switchboard | Internal | E.164-ish |
| `opening_hours` | `jsonb` | Yes | | Weekday → open/close windows | Internal | schema-validated |
| `status` | `varchar(16)` | No | | Lifecycle | Internal | enum (see §11.6) |

Indexes: PK; `UNIQUE(branch_code)`; `(status)`.

Seeded reference rows (all `Africa/Cairo`): `ASW` Aswan / أسوان, `ALX` Alexandria / الإسكندرية, `OCT` 6th of October / السادس من أكتوبر, `MAA` Maadi / المعادي, `DOK` Dokki / الدقي, `NSR` Nasr City / مدينة نصر.

> **`branch` ≠ `provider_location` (§5.2).** A `branch` is a **Mersal-operated internal facility**; a `provider_location` is a **contracted third-party site**. Both may host care, but only branches are subject to staff branch-scoping — external providers stay `ProviderScoped`. The two tables are never merged.

### 10B.2 `user_branch_assignment`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `assignment_id` | `uuid` | No | PK | UUID v7 | Internal | v7 generated |
| `user_id` | `uuid` | No | logical FK | Staff user (`identity`) | Internal | |
| `branch_id` | `uuid` | No | FK | Assigned branch | Internal | FK `provider.branch` |
| `assignment_type` | `varchar(12)` | No | | Home vs additional working branch | Internal | enum (see §11.6) |
| `valid_from` | `date` | No | | Assignment start | Internal | |
| `valid_to` | `date` | Yes | | Assignment end (null = open) | Internal | ≥ `valid_from` |
| `status` | `varchar(12)` | No | | Lifecycle | Internal | enum (see §11.6) |

Indexes: PK; **partial `UNIQUE(user_id) WHERE assignment_type='Home' AND status='Active'`** (exactly one active Home branch per user); `UNIQUE(user_id, branch_id) WHERE status='Active'`; `(branch_id, status)`.

> **Permitted set** = Home ∪ Additional, filtered to `status='Active'` and within the validity window. The `X-Active-Branch` header is validated against it on every request — never trusted; a mismatch is `403` + audited `BranchScopeDenied`. Revocation takes effect on the next request.

### 10B.3 `practitioner`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `practitioner_id` | `uuid` | No | PK | UUID v7 | Internal | v7 generated |
| `user_id` | `uuid` | No | logical FK, UK | Backing identity user | Internal | `UNIQUE` — one profile per user |
| `practitioner_type` | `varchar(12)` | No | | Clinical role class | Internal | enum (see §11.6) |
| `full_name_en` | `varchar(160)` | No | | English display name | PII | non-empty |
| `full_name_ar` | `varchar(160)` | Yes | | Arabic display name | PII | |
| `license_no` | `varchar(40)` | Yes | | Professional licence number | PII | unique per authority |
| `license_expiry` | `date` | Yes | | Licence expiry (feeds credential reminders) | Internal | ≥ issue date |
| `status` | `varchar(16)` | No | | Lifecycle | Internal | enum: Active/Suspended/Inactive |

Indexes: PK; `UNIQUE(user_id)`; `(status)`; partial `(license_expiry) WHERE status='Active'`.

### 10B.4 `specialty` / `practitioner_specialty`

| Table.Column | Type | Null | Key | Description | Sens |
|---|---|---|---|---|---|
| `specialty.specialty_code` | `varchar(20)` | No | PK/UK | Stable reference code | Public |
| `specialty.name_en` | `varchar(120)` | No | | English name | Public |
| `specialty.name_ar` | `varchar(120)` | No | | Arabic name | Public |
| `specialty.parent_code` | `varchar(20)` | Yes | FK | Parent specialty (sub-specialty tree) | Public |
| `practitioner_specialty.practitioner_id` | `uuid` | No | PK/FK | | Internal |
| `practitioner_specialty.specialty_code` | `varchar(20)` | No | PK/FK | | Internal |
| `practitioner_specialty.is_primary` | `boolean` | No | | Exactly one primary per practitioner | Internal |

Indexes: `specialty` PK on `specialty_code`, `(parent_code)`; `practitioner_specialty` composite PK `(practitioner_id, specialty_code)`, **partial `UNIQUE(practitioner_id) WHERE is_primary`**, `(specialty_code)`.

> A practitioner carries **≥ 1** specialty with exactly one flagged primary. Specialty drives referral routing, the doctor picker (filtered by active branch **+** specialty), utilization reporting, and default examination-type suggestions.

### 10B.5 `practitioner_branch_assignment`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `practitioner_id` | `uuid` | No | PK/FK | | Internal | FK `practitioner` |
| `branch_id` | `uuid` | No | PK/FK | | Internal | FK `branch` |
| `valid_from` | `date` | No | | | Internal | |
| `valid_to` | `date` | Yes | | Null = open-ended | Internal | ≥ `valid_from` |
| `status` | `varchar(12)` | No | | Lifecycle | Internal | enum: Active/Revoked |

Indexes: composite PK `(practitioner_id, branch_id)`; `(branch_id, status)`.

> A doctor may serve **one or many** branches. Availability creation **and** booking validate the doctor→branch assignment; booking at an unassigned branch fails `422` with an explicit reason.

### 10B.6 `examination_type` (`masterdata` — reference)

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `examination_type_id` | `uuid` | No | PK | UUID v7 | Public | v7 generated |
| `code` | `varchar(30)` | No | UK | Stable examination code | Public | `UNIQUE` |
| `name_en` | `varchar(160)` | No | | English name | Public | non-empty |
| `name_ar` | `varchar(160)` | No | | Arabic name | Public | non-empty |
| `category` | `varchar(16)` | No | | Examination category | Public | enum (see §11.6) |
| `default_code_system` | `varchar(10)` | Yes | | Billing/terminology system | Public | enum: CPT/LOINC/LOCAL |
| `default_code` | `varchar(20)` | Yes | | Default code in that system | Public | exists in masterdata |
| `sensitivity_level` | `varchar(20)` | No | | Disclosure class, default `'Standard'` | Public | enum (see §11.6) |
| `sensitive_category` | `varchar(24)` | Yes | | Special-category type; **null when `sensitivity_level='Standard'`** | Public | enum (see §11.6) |
| `status` | `varchar(16)` | No | | Lifecycle | Public | enum: Active/Retired |

Indexes: PK; `UNIQUE(code)`; `(category, status)`; `(sensitivity_level)`; `CHECK (sensitivity_level = 'Standard' OR sensitive_category IS NOT NULL)`.

> The **classification table is configuration, not code** — the Medical Director + DPO ratify the category list ([37 §5](37-branch-scoping-and-clinical-sensitivity.md), [20-compliance-checklist.md](20-compliance-checklist.md)).

### 10B.7 `report_access_request`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `request_id` | `uuid` | No | PK | UUID v7 | Internal | v7 generated |
| `result_ref` | `uuid` | No | logical FK | Target result / fulfillment reference | **PHI (link)** | exactly one result per request |
| `document_id` | `uuid` | Yes | logical FK | Report blob, when the request targets a document | **PHI (link)** | must belong to `result_ref` |
| `beneficiary_id` | `uuid` | No | logical FK | Data subject | **PHI (link)** | validated via event |
| `requested_by` | `uuid` | No | logical FK | Requesting user | Internal | |
| `requested_for_role` | `varchar(40)` | No | | Role the access is needed in | Internal | enum from [10-role-matrix.md](10-role-matrix.md) |
| `purpose_code` | `varchar(24)` | No | | Lawful purpose of the disclosure | Internal | enum (see §11.6); **mandatory** |
| `justification` | `text` | **No** | | Free-text clinical/operational rationale | **PHI** | **mandatory**, non-empty; minimized in exports |
| `requested_ttl_hours` | `integer` | Yes | | Requested grant duration | Internal | 1 ≤ h ≤ policy max |
| `status` | `varchar(16)` | No | | Lifecycle | Internal | enum (see §11.6) |
| `decided_by` | `uuid` | Yes | logical FK | Deciding user | Internal | authoring doctor OR Medical Director |
| `decided_by_role` | `varchar(24)` | Yes | | Authority the decision was made under | Internal | `AuthoringDoctor` / `MedicalDirector` |
| `decided_at` | `timestamptz` | Yes | | Decision time (UTC) | Internal | ≥ `created_at` |
| `decision_reason` | `text` | Yes | | **Mandatory on Deny**; optional otherwise | **PHI** | required when `status='Denied'` |

Indexes: PK; `(result_ref, status)`; `(requested_by, status)`; `(beneficiary_id)`; partial `(status) WHERE status IN ('Requested','UnderReview','InfoRequested')` (decision worklist).

> A request without **both** `purpose_code` and `justification` is rejected at validation — never persisted as a pending request. `decided_by_role='MedicalDirector'` decisions are **extra-audited** ([19-audit-strategy.md](19-audit-strategy.md)).

### 10B.8 `report_access_grant`

| Column | Type | Null | Key | Description | Sens | Validation |
|---|---|---|---|---|---|---|
| `grant_id` | `uuid` | No | PK | UUID v7 | Internal | v7 generated |
| `request_id` | `uuid` | No | FK | Approved request | Internal | FK `report_access_request`; status `Approved` |
| `grantee_user_id` | `uuid` | No | logical FK | **Sole** authorized reader — non-transferable | Internal | = `request.requested_by` |
| `result_ref` | `uuid` | No | logical FK | **Single** result covered | **PHI (link)** | = `request.result_ref` |
| `purpose_code` | `varchar(24)` | No | | Copied from the request (purpose limitation) | Internal | enum (see §11.6) |
| `granted_at` | `timestamptz` | No | | Grant start (UTC) | Internal | |
| `expires_at` | `timestamptz` | No | | TTL end — default **72h** `Sensitive`, **24h** `HighlySensitive` | Internal | > `granted_at` |
| `revoked_at` | `timestamptz` | Yes | | Early revocation | Internal | ≥ `granted_at` |
| `revoked_by` | `uuid` | Yes | logical FK | Author / Medical Director / DPO | Internal | required when `revoked_at` set |

Indexes: PK; `UNIQUE(request_id)`; **`(grantee_user_id, result_ref) WHERE revoked_at IS NULL`** (live-grant lookup on every read); partial `(expires_at) WHERE revoked_at IS NULL` (expiry sweep).

> A grant is **time-boxed, single-result, and non-transferable**. **Every read under a grant** emits a `SensitiveResultReadUnderGrant` audit event carrying `grant_id`, `purpose_code`, and actor — separately from ordinary PHI-read audit. Grants are never extended: a longer need is a **new** request.

### 10B.9 Additive columns on existing tables

Additive only — all new columns are nullable or carry a default, so migrations are expand/contract-safe.

| Table (§) | New column | Type | Null | Description | Sens |
|---|---|---|---|---|---|
| `emr.appointment` (§6.1) | `branch_id` | `uuid` | Yes | Mersal branch hosting the appointment | Internal |
| `emr.appointment_slot` | `branch_id` | `uuid` | Yes | Branch the slot belongs to | Internal |
| `provider.provider_availability` | `branch_id` | `uuid` | Yes | Branch the availability applies to | Internal |
| `emr.waitlist_entry` | `branch_id` | `uuid` | Yes | Branch the entry queues against | Internal |
| `emr.encounter` (§6.2) | `branch_id` | `uuid` | Yes | Branch where the visit occurred | Internal |
| `emr.queue_ticket` | `branch_id` | `uuid` | Yes | Branch owning the queue | Internal |
| `orders.investigation_order` (§7.1) | `ordering_branch_id` | `uuid` | Yes | Branch the order originated from | Internal |
| `orders.investigation_order` (§7.1) | `examination_type_id` | `uuid` | Yes | FK `masterdata.examination_type` | Internal |
| `orders.investigation_order` (§7.1) | `sensitivity_level` | `varchar(20)` | No | Denormalized, `DEFAULT 'Standard'` | Internal |
| `orders.order_line` (§7.2) | `examination_type_id` | `uuid` | Yes | Per-line examination type | Internal |
| `orders.order_line` (§7.2) | `sensitivity_level` | `varchar(20)` | No | Denormalized, `DEFAULT 'Standard'` | Internal |
| `orders.order_fulfillment` / result (§7.3) | `examination_type_id` | `uuid` | Yes | Inherited from the line | Internal |
| `orders.order_fulfillment` / result (§7.3) | `sensitivity_level` | `varchar(20)` | No | Denormalized, `DEFAULT 'Standard'` | Internal |

Indexes added: `(branch_id, scheduled_start)` on `appointment`; `(branch_id, status)` on `appointment_slot`, `waitlist_entry`, `queue_ticket`; `(branch_id, started_at)` on `encounter`; `(ordering_branch_id, status)` on `investigation_order`; partial `(sensitivity_level) WHERE sensitivity_level <> 'Standard'` on order/line/result.

> **`branch_id` nullable ⇒ NULL means an *external provider location*, not a Mersal branch.** Rows with a null `branch_id` are out of scope for branch filtering and remain governed by `ProviderOwnership` ([37 §3](37-branch-scoping-and-clinical-sensitivity.md)).
>
> **Sensitivity is pinned at creation.** `sensitivity_level` is copied onto the order, its lines, and the resulting result/report **at the moment the order is created**, and is never recomputed from `examination_type` afterwards. Reclassifying an examination type later changes *future* orders only — it can **never retroactively unlock** data that was captured under a stricter class (and a later tightening is applied by an explicit, audited re-classification job, never silently). Denormalization also means gating never depends on a cross-service join at read time.
>
> **Clinical result values for non-`Standard` results are PHI with restricted disclosure** — existence metadata only for everyone except the authoring/ordering doctor (with treating relationship), the beneficiary, and a live `report_access_grant` holder.

---

## 10C. Domain: Patient Profile & Call History (`callcentre`, `policy` schemas) — Phase 20

`profile-service` appears in no table here, and that is the entry: it **owns no data**. The unified patient
profile is composed at read time from the services that do (design 39 §7.4), so phase 20 adds exactly two
things to the physical model — both to schemas that already existed.

### 10C.1 `callcentre.call_interaction` — the `summary` column

| Column | Type | Null | Notes |
|---|---|---|---|
| `summary` | `varchar(500)` | Y | The **operational account** of the call, written at wrap-up and read by OTHER roles through the patient profile. **Required at close unless `outcome = 'Abandoned'`** (422 otherwise). Capped so it stays a summary rather than becoming a second notes field. |
| `summary_edited_at` | `timestamptz` | Y | Set on the first correction; drives the visible "edited" marker. |
| `summary_edited_by` | `text` | Y | Who corrected it. |

**Why this is a new column and not a reuse of `notes`.** Phase 20 widens the audience for call history to
coordinators, approvers and clinicians. `notes` is the agent's working text — typed mid-call, unedited, written
under the reasonable assumption that only the call centre would ever read it. Promoting that column to the new
audience would have been a silent, retroactive disclosure of years of it. `notes` stays exactly where it was
and is **never** projected to another role at any level.

**Clinical content does not belong in `summary`.** Agents are not clinicians, and a summary reading "complained
of chest pain" creates an unreviewed clinical record in an operational store. The UI states this at the point
of writing; genuine clinical escalation goes through the case/escalation path.

### 10C.2 `callcentre.call_summary_revision` — append-only corrections

| Column | Type | Null | Notes |
|---|---|---|---|
| `revision_id` | `uuid` | N | PK. |
| `interaction_id` | `uuid` | N | FK → `call_interaction`. |
| `tenant_id` | `text` | N | RLS scope (fail-closed, matching callcentre 0003). |
| `previous_value` | `varchar(500)` | Y | What it said before. |
| `new_value` | `varchar(500)` | Y | What it says now. |
| `edited_by` / `edited_at` | `text` / `timestamptz` | Y / N | Who and when. |

There is no update or delete path in the API: the table is written to and read from, never rewritten. A summary
other roles rely on that can be corrected without trace is worse than no summary, because it still reads as a
record.

### 10C.3 `policy.DocumentClass` — `IdentityPhoto`

A new value on the phase-19.3b document-class enum (§10B): the beneficiary's identification photograph.
`Administrative` by visibility class, but with **its own, much narrower role allow-list** — reception, the call
centre, treating clinicians and beneficiary management. Finance, claims, labs, pharmacies and platform admins
receive a header with **no photo field at all**.

Three properties are enforced in code, not convention:

1. **Consent-gated at upload** — an `IdentityPhoto` is only stored when an active `ConsentForm` is on file for
   that member. Refusal is permitted and **must not block care**; the profile simply shows initials.
2. **Never listed with other documents** — it has its own endpoint, so it is not handed to every role entitled
   to see that a consent form exists.
3. **Short-TTL signed retrieval, always audited** — five minutes, minted per request as the caller. A permanent
   URL would outlive the session, the role and the consent that produced it.

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
| **Claim status** | Draft, Submitted, UnderAdjudication, PendingInfo, ClinicalReview, Approved, PartiallyApproved, Denied, Settled, Appealed, Void |
| **Claim Line status** | Pending, Approved, PartiallyApproved, Denied, Adjusted, Void |
| **Claim Batch status** | Open, UnderReview, Decided, SettlementIssued, Closed, Cancelled |
| **Reimbursement status** | Submitted, OcrProcessing, AutoMatched, ManualAssessment, Adjudicating, Approved, PartiallyApproved, Denied, Paid (recorded), Void |

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

### 11.5 Claims enums (`claims` schema — see [36-claims-management.md](36-claims-management.md))

| Enum | Values |
|---|---|
| Claim origin | AutoDerived, ProviderSubmitted, Reimbursement |
| Fulfillment type | OrderFulfillment, DispenseEvent, None |
| Claim code systems | CPT, LOINC, LOCAL, DRUG |
| System recommendation | RecommendApprove, RecommendPartial, RecommendDeny, RequiresManualReview |
| Claim decision | Approve, PartiallyApprove, Deny, Adjust, RequestInfo, RouteToClinical |
| Adjustment type | PriceCorrection, QuantityCorrection, Deduction, Recovery, Clawback, Writeoff, Reversal, Void, Reallocation |
| Batch type | Provider, Reimbursement |
| Batch selection mode | DateRange, ProviderBranch, ProviderGroup, Manual |
| Reimbursement match method | AutoOcr, Manual, Unmatched |
| Claim document type | Invoice, Receipt, ResultProof, DispenseProof, Statement, SettlementAdvice, Other |

**Claim denial / reason codes** (adjudication collects **all** applicable codes per line, never stopping at the first failure — [36 §5](36-claims-management.md)):

| Code | Raised when |
|---|---|
| `NOT_ELIGIBLE` | Beneficiary not eligible on the service date |
| `POLICY_EXPIRED` | Policy/coverage not in effect on the service date |
| `NOT_COVERED_CATEGORY` | Service `benefit_category` not covered |
| `NO_PRIOR_AUTH` | Gated service with no authorization |
| `AUTH_EXPIRED` | Authorization expired before the service date |
| `EXCEEDS_AUTH_SCOPE` | Line falls outside a `PartiallyApproved` authorized scope |
| `NO_FULFILLMENT_RECORD` | No matching `order_fulfillment` / `dispense_event` |
| `DUPLICATE_CLAIM` | A live payable line already exists for the fulfillment reference |
| `PROVIDER_OUT_OF_NETWORK` | Provider not active in the network on the service date |
| `CONTRACT_NOT_EFFECTIVE` | No in-effect contract for the provider on the service date |
| `NO_TARIFF` | No `contract_service_line.agreed_price` for the code/date → **manual pricing, never a guessed price** |
| `LIMIT_EXCEEDED` | Coverage limit for the `limit_type` exhausted |
| `NOT_MEDICALLY_NECESSARY` | Recorded by a **clinical reviewer** after `RouteToClinical` (never by a Claims Officer) |
| `ILLEGIBLE_DOCUMENT` | Receipt/invoice unreadable or OCR unusable |
| `RECEIPT_MISMATCH` | Receipt does not match the authorized order/prescription or the fulfillment record |

### 11.6 Branch, practitioner & sensitivity enums (`provider` / `masterdata` / `orders` schemas — see [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md))

| Enum | Values |
|---|---|
| Branch status | Active, Suspended, Closed |
| Branch assignment type | Home, Additional |
| Branch assignment status | Active, Revoked |
| Practitioner type | Doctor, Nurse |
| Practitioner branch assignment status | Active, Revoked |
| Examination category | Lab, Imaging, Procedure, Consultation, Assessment |
| **Sensitivity level** | Standard, Sensitive, HighlySensitive |
| **Sensitive category** | MentalHealth, HIV_STI, Genetic, SubstanceUse, ReproductiveHealth, GBV_Forensic, Other |
| **Purpose code** | ContinuityOfCare, AuthorizationDecision, ClinicalReview, Complaint, Legal, Other |
| **Report access request status** | Requested, UnderReview, InfoRequested, Approved, Denied, Expired, Revoked |
| Scope mode | BranchScoped, MemberScoped, ProviderScoped |

**Scope mode** is an authorization attribute, not a stored column: `BranchScoped` roles (Reception, Appointment Coordinator, Nurse, Doctor worklists, Branch/Clinic Manager) are filtered server-side to the **active branch**; `MemberScoped` roles (Approvals, Medical Director, Case Manager, Finance/Claims, Network, Admin, Reporting) span **all branches** with branch as an optional convenience filter; `ProviderScoped` (external labs/imaging/pharmacies) is unchanged and unaffected by the branch dimension ([37 §3](37-branch-scoping-and-clinical-sensitivity.md), [11-permission-matrix.md](11-permission-matrix.md)).

> **Sensitivity is pinned, not looked up.** `sensitivity_level` is denormalized onto `investigation_order`, `order_line`, and the result at **order creation**; a later reclassification of the `examination_type` never retroactively unlocks previously restricted data. Clinical **result values** for `Sensitive` / `HighlySensitive` results are **PHI with restricted disclosure** — existence metadata only outside the authoring doctor, the beneficiary, and a live `report_access_grant`.

---

## 11.7 User & access model tables (phase 21 — see [40-user-access-model.md](40-user-access-model.md), ADR-0021)

The security principal is the **membership**, not the identity (invariant 1): authorization evaluates against
`identity.tenant_membership`, and one person may hold several with genuinely different authority.

| Table | Schema | Purpose | Notes |
|---|---|---|---|
| `tenant_membership` | identity | **The security principal** — (identity × tenant), owning roles, provider binding and lifecycle | Status `Invited/Active/Suspended/Ended`; only `Active` is selectable. One live row per (user, tenant). Deliberately **not** RLS-protected — the issuer resolves logins before any `app.tenant_id` exists |
| `membership_role` | identity | Roles held THROUGH a membership | Replaces the identity-level `user_role` binding (expand phase: both exist) |
| `tenant_membership_history` | identity | Append-only lifecycle history | Memberships are never hard-deleted |
| `membership_override` | identity | Per-membership Allow/Deny of one catalog key | `reason` is **NOT NULL** — an unexplained exception cannot be reviewed. `valid_until` evaluated at resolution time (no sweeper). One live row per (membership, key) |
| `membership_override_history` | identity | Append-only override history | |
| `scope` (extended) | identity | Catalog metadata | Gains `deprecated`, `replaced_by`, `is_platform_admin_key`. **A1:** the platform-admin flag short-circuits ONLY keys marked `is_platform_admin_key`; every other key is hard-excluded |
| `role` (extended) | identity | Ordinal trust tier | Gains `level int` — **lower = more privileged**, seeded as `4 − sensitivity tier`. Answers tier-shaped questions only; capability questions use KEYS |
| `user_session` | identity | Live sign-ins + device metadata | Soft, attributed revocation. Concurrent cap revokes the **oldest** |
| `login_attempt` | identity | Sign-in history, successes and failures | Never any password material. `failure_reason` is COARSE — "no such user" and "wrong password" record the same value, so the distinction cannot leak as an enumeration oracle |
| `branch_scope_grant` | admin | Time-bounded, attributed branch reach | Replaces `user_branch_assignment` (expand phase: copied, source still authoritative). Keyed on `branch_id uuid`, not a code |
| `branch_scope_grant_history` | admin | Append-only grant history | |
| `tenant_feature` / `tenant_feature_history` | admin | Per-tenant programme switches | Absent ⇒ **disabled** (fail closed) |
| `tenant_limit` / `tenant_limit_history` | admin | Per-tenant caps | Absent ⇒ **unlimited** (fail open — inventing a default would take a working platform offline). Enforced by counting live rows inside the mutating transaction, never a stored counter |

**Effective set = (role grants ∪ membership allows) − membership denies.** Deny always wins. One evaluator
(`libs/authz/EffectiveSetEvaluator`), two entry points (token issuance and out-of-session), parity-tested.

---

## 12. Reference / Lookup Tables Summary

| Lookup | Owner schema | Distribution |
|---|---|---|
| `benefit_category` | policy | replicated read |
| `icd_code`, `cpt_code`, `loinc_code` | masterdata | cache + OpenSearch |
| `drug`, `atc_class`, `drug_interaction`, `allergen` | masterdata | cache + OpenSearch |
| `notification_template` | notification | local |
| `role`, `permission` | identity | local |
| `branch` | provider | replicated read (6 seeded rows) |
| `specialty` | provider | replicated read |
| `examination_type` | masterdata | cache + replicated read |

---

## 13. Cross-References

- Structural model & indexes: [15-database-erd.md](15-database-erd.md)
- Transitions/guards for status enums: [23-state-machines.md](23-state-machines.md)
- Claims module design (origination, batching, adjudication, settlement): [36-claims-management.md](36-claims-management.md)
- Branch scoping, practitioner specialty & sensitivity gating (§10B / §11.6): [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md)
- Sensitivity handling, RLS, masking, minimization: [18-security-model.md](18-security-model.md)
- API field shapes: [17-api-specifications.md](17-api-specifications.md)
