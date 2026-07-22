# 17 — API Specifications

> Part of the **Mersal Healthcare Benefit Management Platform (HBMP)** design workspace.
> Up: [00-README-INDEX.md](00-README-INDEX.md) · Foundations: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [15-database-erd.md](15-database-erd.md) · [16-service-architecture.md](16-service-architecture.md) · [23-state-machines.md](23-state-machines.md) · [18-security-model.md](18-security-model.md)

---

## 1. Conventions

- **Style:** REST, resource-oriented, JSON. **OpenAPI 3.1**. **FHIR R4-aligned** where practical (see §12).
- **Base path:** `https://api.mersal-hbmp.org/api/v1`. Version in the path (`/api/v1`); breaking changes → `/api/v2`.
- **Media types:** `application/json`; problem responses `application/problem+json` (RFC 7807).
- **IDs:** resource IDs are UUID v7; business keys (`MRS-M-*`, `ENC-*`, `ORD-*`, `RX-*`, `AUTH-*`, `REF-*`) are returned and can be used as alternate lookups.
- **Timestamps:** RFC 3339 UTC (`2026-07-21T10:15:00Z`).

### 1.1 Pagination

Cursor-based for large/append-heavy collections; offset optional for small ones.

```
GET /beneficiaries?limit=50&cursor=eyJvZmZzZXQiOjUwfQ
```

Response envelope:
```json
{
  "data": [ ... ],
  "page": { "limit": 50, "nextCursor": "eyJvZmZzZXQiOjEwMH0", "hasMore": true }
}
```

### 1.2 Filtering & sorting

`?status=Active&order_type=Lab&sort=-requestedAt`. Allowed filter fields are documented per endpoint; unknown fields → `400`.

### 1.3 Errors (RFC 7807)

```json
{
  "type": "https://api.mersal-hbmp.org/problems/over-consumption",
  "title": "Quantity exceeds remaining",
  "status": 409,
  "detail": "order_line ORD-...-L1 has 0 remaining of 1 ordered",
  "instance": "/investigation-orders/ORD-2026-000123/consume",
  "correlationId": "corr-abc",
  "errors": [ { "field": "quantity", "code": "OVER_CONSUME" } ]
}
```

Standard statuses: `200/201/204`, `400` validation, `401` unauthenticated, `403` scope/RLS, `404`, `409` conflict/invariant, `412` precondition (ETag), `422` semantic, `429` rate limit, `5xx`.

### 1.4 Idempotency

Unsafe retriable POSTs (**consume, dispense, decision, notification send, registration**) require `Idempotency-Key: <uuid>`. The server stores key + request hash + response for 24h; replays return the stored response. Same key + different body → `409`.

### 1.5 Concurrency

Mutable resources return `ETag: "<row_version>"`. Updates send `If-Match`; mismatch → `412`.

---

## 2. AuthN / AuthZ (OAuth2 scopes)

Bearer JWT from **Keycloak**, validated at Kong. Scopes map to RBAC permissions ([15-database-erd.md](15-database-erd.md) §11, [18-security-model.md](18-security-model.md)).

| Scope | Grants | Typical role |
|---|---|---|
| `patient.read` / `patient.write` | Beneficiary CRUD | Case Worker |
| `eligibility.check` | Run eligibility | Case Worker, Clinician |
| `emr.read` / `emr.write` | Encounters, notes, vitals | Clinician |
| `orders.write` / `orders.consume` | Create / fulfill orders | Clinician / Provider staff |
| `rx.write` / `rx.dispense` | Prescribe / dispense | Prescriber / Pharmacist |
| `auth.decide` | Approve/reject authorizations | Approver |
| `provider.admin` | Manage providers/contracts | Admin |
| `report.read` | Reporting APIs | Admin, Auditor |

```yaml
components:
  securitySchemes:
    oauth2:
      type: oauth2
      flows:
        authorizationCode:
          authorizationUrl: https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize
          tokenUrl: https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token
          scopes:
            patient.read: Read beneficiaries
            patient.write: Write beneficiaries
            eligibility.check: Run eligibility checks
            orders.write: Create investigation orders
            orders.consume: Consume order lines
            rx.write: Create prescriptions
            rx.dispense: Dispense prescriptions
            auth.decide: Decide authorizations
```

---

## 3. Shared Components (OpenAPI)

```yaml
components:
  parameters:
    IdempotencyKey:
      name: Idempotency-Key
      in: header
      required: true
      schema: { type: string, format: uuid }
    IfMatch:
      name: If-Match
      in: header
      required: true
      schema: { type: string }
  schemas:
    Problem:
      type: object
      properties:
        type: { type: string, format: uri }
        title: { type: string }
        status: { type: integer }
        detail: { type: string }
        instance: { type: string }
        correlationId: { type: string }
        errors:
          type: array
          items:
            type: object
            properties:
              field: { type: string }
              code: { type: string }
    Page:
      type: object
      properties:
        limit: { type: integer }
        nextCursor: { type: string, nullable: true }
        hasMore: { type: boolean }
  responses:
    Problem400: { description: Validation error, content: { application/problem+json: { schema: { $ref: '#/components/schemas/Problem' } } } }
    Problem409: { description: Conflict / invariant violation, content: { application/problem+json: { schema: { $ref: '#/components/schemas/Problem' } } } }
```

---

## 4. Patient / Beneficiaries

```yaml
paths:
  /beneficiaries:
    post:
      summary: Register beneficiary
      security: [ { oauth2: [ patient.write ] } ]
      parameters: [ { $ref: '#/components/parameters/IdempotencyKey' } ]
      requestBody:
        required: true
        content:
          application/json:
            schema: { $ref: '#/components/schemas/BeneficiaryCreate' }
      responses:
        '201':
          description: Created
          headers: { ETag: { schema: { type: string } } }
          content: { application/json: { schema: { $ref: '#/components/schemas/Beneficiary' } } }
        '400': { $ref: '#/components/responses/Problem400' }
        '409': { $ref: '#/components/responses/Problem409' }
    get:
      summary: Search beneficiaries
      security: [ { oauth2: [ patient.read ] } ]
      parameters:
        - { name: identifierType, in: query, schema: { type: string, enum: [NationalID, Passport, RefugeeID, UNHCRNo, MemberNo] } }
        - { name: identifierValue, in: query, schema: { type: string } }
        - { name: name, in: query, schema: { type: string } }
        - { name: status, in: query, schema: { type: string } }
        - { name: limit, in: query, schema: { type: integer, default: 50, maximum: 200 } }
        - { name: cursor, in: query, schema: { type: string } }
      responses:
        '200':
          description: OK
          content:
            application/json:
              schema:
                type: object
                properties:
                  data: { type: array, items: { $ref: '#/components/schemas/Beneficiary' } }
                  page: { $ref: '#/components/schemas/Page' }
  /beneficiaries/{id}:
    get:
      summary: Get beneficiary
      security: [ { oauth2: [ patient.read ] } ]
      parameters: [ { name: id, in: path, required: true, schema: { type: string, format: uuid } } ]
      responses:
        '200': { description: OK, content: { application/json: { schema: { $ref: '#/components/schemas/Beneficiary' } } } }
        '404': { description: Not found }
    patch:
      summary: Update beneficiary
      security: [ { oauth2: [ patient.write ] } ]
      parameters:
        - { name: id, in: path, required: true, schema: { type: string, format: uuid } }
        - { $ref: '#/components/parameters/IfMatch' }
      responses:
        '200': { description: Updated }
        '412': { description: Version conflict }
  /beneficiaries/{id}/identifiers:
    post:
      summary: Add identifier
      security: [ { oauth2: [ patient.write ] } ]
      responses: { '201': { description: Created }, '409': { $ref: '#/components/responses/Problem409' } }

components:
  schemas:
    BeneficiaryCreate:
      type: object
      required: [ givenName, familyName, birthDate, sex, identifiers ]
      properties:
        givenName: { type: string }
        familyName: { type: string }
        birthDate: { type: string, format: date }
        sex: { type: string, enum: [ male, female, other, unknown ] }
        nationalityCode: { type: string }
        identifiers:
          type: array
          minItems: 1
          items: { $ref: '#/components/schemas/IdentifierInput' }
        contacts:
          type: array
          items: { $ref: '#/components/schemas/ContactInput' }
    IdentifierInput:
      type: object
      required: [ type, value ]
      properties:
        type: { type: string, enum: [ NationalID, Passport, RefugeeID, UNHCRNo, MemberNo ] }
        value: { type: string }
        issuingCountry: { type: string }
    ContactInput:
      type: object
      properties:
        type: { type: string, enum: [ Phone, Email, Address, EmergencyContact ] }
        value: { type: string }
    Beneficiary:
      type: object
      properties:
        beneficiaryId: { type: string, format: uuid }
        memberNo: { type: string, example: MRS-M-2026-000045 }
        givenName: { type: string }
        familyName: { type: string }
        birthDate: { type: string, format: date }
        sex: { type: string }
        status: { type: string, enum: [ Pending, Active, Suspended, Expired, Blocked, Inactive ] }
        identifiers: { type: array, items: { $ref: '#/components/schemas/IdentifierInput' } }
```

---

## 5. Eligibility

```yaml
paths:
  /eligibility/check:
    post:
      summary: Check eligibility for a benefit
      security: [ { oauth2: [ eligibility.check ] } ]
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required: [ beneficiaryId, benefitCategory ]
              properties:
                beneficiaryId: { type: string, format: uuid }
                benefitCategory: { type: string, example: LAB }
                serviceCode: { type: string, example: "80053" }
      responses:
        '200':
          description: Decision
          content:
            application/json:
              schema:
                type: object
                properties:
                  decision: { type: string, enum: [ Eligible, Ineligible, NeedsAuthorization ] }
                  coverageId: { type: string, format: uuid }
                  reasons: { type: array, items: { type: string } }
                  limitState:
                    type: object
                    properties:
                      limitType: { type: string }
                      limitValue: { type: number }
                      consumedValue: { type: number }
                      remaining: { type: number }
                  snapshotExpiresAt: { type: string, format: date-time }
```

Cache-first (Valkey); `NeedsAuthorization` triggers the approval flow ([16-service-architecture.md](16-service-architecture.md) §8).

---

## 6. Appointments & Encounters / EMR

```yaml
paths:
  /appointments:
    post:
      summary: Book appointment
      security: [ { oauth2: [ emr.write ] } ]
      responses: { '201': { description: Created } }
  /encounters:
    post:
      summary: Open encounter
      security: [ { oauth2: [ emr.write ] } ]
      requestBody:
        content:
          application/json:
            schema:
              type: object
              required: [ beneficiaryId, encounterClass ]
              properties:
                beneficiaryId: { type: string, format: uuid }
                appointmentId: { type: string, format: uuid, nullable: true }
                encounterClass: { type: string, enum: [ Ambulatory, Emergency, Inpatient, Virtual ] }
      responses:
        '201':
          description: Created
          content:
            application/json:
              schema:
                type: object
                properties:
                  encounterId: { type: string, format: uuid }
                  encounterNo: { type: string, example: ENC-2026-000777 }
                  status: { type: string, enum: [ InProgress, Finished, Cancelled ] }
  /encounters/{id}/notes:
    post:
      summary: Add SOAP note
      security: [ { oauth2: [ emr.write ] } ]
      requestBody:
        content:
          application/json:
            schema:
              type: object
              properties:
                noteType: { type: string, enum: [ SOAP, Progress, Nursing ] }
                subjective: { type: string }
                objective: { type: string }
                assessment: { type: string }
                plan: { type: string }
      responses: { '201': { description: Created } }
  /encounters/{id}/diagnoses:
    post:
      summary: Record diagnosis
      security: [ { oauth2: [ emr.write ] } ]
      requestBody:
        content:
          application/json:
            schema:
              type: object
              required: [ icdCode ]
              properties:
                icdCode: { type: string, example: E11.9 }
                rank: { type: string, enum: [ Primary, Secondary ] }
      responses: { '201': { description: Created } }
  /encounters/{id}/vitals:
    post:
      summary: Record vital
      security: [ { oauth2: [ emr.write ] } ]
      responses: { '201': { description: Created } }
```

---

## 7. Investigation Orders (incl. atomic consume)

```yaml
paths:
  /investigation-orders:
    post:
      summary: Create investigation order
      security: [ { oauth2: [ orders.write ] } ]
      parameters: [ { $ref: '#/components/parameters/IdempotencyKey' } ]
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required: [ beneficiaryId, encounterId, orderType, lines ]
              properties:
                beneficiaryId: { type: string, format: uuid }
                encounterId: { type: string, format: uuid }
                orderType: { type: string, enum: [ Lab, Imaging, Procedure ] }
                lines:
                  type: array
                  minItems: 1
                  items:
                    type: object
                    required: [ codeSystem, code, quantityOrdered ]
                    properties:
                      codeSystem: { type: string, enum: [ CPT, LOINC, LOCAL ] }
                      code: { type: string }
                      quantityOrdered: { type: number, minimum: 1 }
      responses:
        '201':
          description: Created
          content:
            application/json:
              schema: { $ref: '#/components/schemas/InvestigationOrder' }
  /investigation-orders/{id}:
    get:
      summary: Get order
      security: [ { oauth2: [ orders.write ] } ]
      responses: { '200': { description: OK, content: { application/json: { schema: { $ref: '#/components/schemas/InvestigationOrder' } } } } }
  /investigation-orders/{id}/consume:
    post:
      summary: Atomically consume an order line (idempotent)
      description: >
        Consumes quantity from a single order line. Atomic + idempotent: replays with the
        same Idempotency-Key return the original result. Over-consumption returns 409.
      security: [ { oauth2: [ orders.consume ] } ]
      parameters:
        - { name: id, in: path, required: true, schema: { type: string, format: uuid } }
        - { $ref: '#/components/parameters/IdempotencyKey' }
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required: [ orderLineId, quantity ]
              properties:
                orderLineId: { type: string, format: uuid }
                quantity: { type: number, minimum: 0.001 }
                performingProviderId: { type: string, format: uuid }
                resultDocumentId: { type: string, format: uuid, nullable: true }
      responses:
        '200':
          description: Consumed (or idempotent replay)
          content:
            application/json:
              schema:
                type: object
                properties:
                  fulfillmentId: { type: string, format: uuid }
                  orderLineId: { type: string, format: uuid }
                  quantityConsumed: { type: number }
                  quantityRemaining: { type: number }
                  lineStatus: { type: string, enum: [ Active, PartiallyUsed, Completed ] }
                  orderStatus: { type: string }
        '409':
          description: Over-consumption or line not active
          content: { application/problem+json: { schema: { $ref: '#/components/schemas/Problem' } } }
        '422': { description: Order not in Active/PartiallyUsed state }

components:
  schemas:
    InvestigationOrder:
      type: object
      properties:
        orderId: { type: string, format: uuid }
        orderNo: { type: string, example: ORD-2026-000123 }
        status: { type: string, enum: [ Requested, PendingApproval, Approved, Rejected, Active, PartiallyUsed, Completed, Expired, Cancelled ] }
        lines:
          type: array
          items:
            type: object
            properties:
              orderLineId: { type: string, format: uuid }
              code: { type: string }
              quantityOrdered: { type: number }
              quantityConsumed: { type: number }
              status: { type: string }
```

> Invariants (atomic, idempotent, no reuse, no over/duplicate use, full audit) are specified in [23-state-machines.md](23-state-machines.md) and realized as described in [16-service-architecture.md](16-service-architecture.md) §8.1.

---

## 8. Prescriptions (incl. dispense)

```yaml
paths:
  /prescriptions:
    post:
      summary: Create prescription (Draft)
      security: [ { oauth2: [ rx.write ] } ]
      parameters: [ { $ref: '#/components/parameters/IdempotencyKey' } ]
      requestBody:
        content:
          application/json:
            schema:
              type: object
              required: [ beneficiaryId, encounterId, lines ]
              properties:
                beneficiaryId: { type: string, format: uuid }
                encounterId: { type: string, format: uuid }
                lines:
                  type: array
                  items:
                    type: object
                    required: [ drugId, quantityPrescribed ]
                    properties:
                      drugId: { type: string, format: uuid }
                      dose: { type: string }
                      route: { type: string }
                      frequency: { type: string }
                      quantityPrescribed: { type: number }
                      refillsAllowed: { type: integer, default: 0 }
      responses: { '201': { description: Created } }
  /prescriptions/{id}/submit:
    post:
      summary: Submit for approval
      security: [ { oauth2: [ rx.write ] } ]
      responses: { '200': { description: Submitted } }
  /prescriptions/{id}/dispense:
    post:
      summary: Dispense a prescription line (atomic, idempotent)
      security: [ { oauth2: [ rx.dispense ] } ]
      parameters:
        - { name: id, in: path, required: true, schema: { type: string, format: uuid } }
        - { $ref: '#/components/parameters/IdempotencyKey' }
      requestBody:
        content:
          application/json:
            schema:
              type: object
              required: [ prescriptionLineId, quantity ]
              properties:
                prescriptionLineId: { type: string, format: uuid }
                quantity: { type: number, minimum: 0.001 }
                dispensingPharmacyId: { type: string, format: uuid }
                batchNo: { type: string }
      responses:
        '200':
          description: Dispensed (or idempotent replay)
          content:
            application/json:
              schema:
                type: object
                properties:
                  dispenseId: { type: string, format: uuid }
                  quantityDispensed: { type: number }
                  quantityRemaining: { type: number }
                  lineStatus: { type: string, enum: [ Active, PartiallyDispensed, Dispensed ] }
        '409': { $ref: '#/components/responses/Problem409' }
```

---

## 9. Authorizations (incl. decision)

```yaml
paths:
  /authorizations:
    post:
      summary: Request authorization
      security: [ { oauth2: [ orders.write, rx.write ] } ]
      requestBody:
        content:
          application/json:
            schema:
              type: object
              required: [ beneficiaryId, requestedFor, subjectRef ]
              properties:
                beneficiaryId: { type: string, format: uuid }
                requestedFor: { type: string, enum: [ Order, Prescription, Referral ] }
                subjectRef: { type: string, format: uuid }
      responses: { '201': { description: Created } }
    get:
      summary: List authorizations (approver queue)
      security: [ { oauth2: [ auth.decide ] } ]
      parameters:
        - { name: status, in: query, schema: { type: string, enum: [ Draft, Submitted, UnderReview, Approved, PartiallyApproved, Rejected, InfoRequested, Overridden, EmergencyApproved, Expired ] } }
      responses: { '200': { description: OK } }
  /authorizations/{id}/decision:
    post:
      summary: Decide (approve/reject/request-info)
      security: [ { oauth2: [ auth.decide ] } ]
      parameters:
        - { name: id, in: path, required: true, schema: { type: string, format: uuid } }
        - { $ref: '#/components/parameters/IdempotencyKey' }
      requestBody:
        content:
          application/json:
            schema:
              type: object
              required: [ decision ]
              properties:
                decision: { type: string, enum: [ Approve, PartiallyApprove, Reject, RequestInfo, Override, EmergencyApprove ] }
                rationale: { type: string }
                appliedLimits: { type: object, additionalProperties: true }
      responses:
        '200':
          description: Decision recorded
          content:
            application/json:
              schema:
                type: object
                properties:
                  authorizationId: { type: string, format: uuid }
                  authNo: { type: string, example: AUTH-2026-000091 }
                  status: { type: string, enum: [ Approved, PartiallyApproved, Rejected, InfoRequested, Overridden, EmergencyApproved ] }
        '409': { description: Already decided }
```

---

## 10. Providers, Referrals, Notifications, Reporting

```yaml
paths:
  /providers:
    get:
      summary: List/search providers
      security: [ { oauth2: [ patient.read ] } ]
      responses: { '200': { description: OK } }
    post:
      summary: Onboard provider
      security: [ { oauth2: [ provider.admin ] } ]
      responses: { '201': { description: Created } }
  /providers/{id}/contracts:
    post:
      summary: Add contract
      security: [ { oauth2: [ provider.admin ] } ]
      responses: { '201': { description: Created } }
  /referrals:
    post:
      summary: Create referral
      security: [ { oauth2: [ emr.write ] } ]
      requestBody:
        content:
          application/json:
            schema:
              type: object
              required: [ beneficiaryId, toProviderId, specialty ]
              properties:
                beneficiaryId: { type: string, format: uuid }
                fromProviderId: { type: string, format: uuid }
                toProviderId: { type: string, format: uuid }
                specialty: { type: string }
      responses:
        '201':
          description: Created
          content:
            application/json:
              schema:
                type: object
                properties:
                  referralNo: { type: string, example: REF-2026-000210 }
                  status: { type: string, enum: [ Requested, Accepted, Scheduled, Completed, Rejected, Cancelled, Expired ] }
  /notifications:
    post:
      summary: Send notification (internal/service)
      security: [ { oauth2: [ report.read ] } ]
      parameters: [ { $ref: '#/components/parameters/IdempotencyKey' } ]
      responses: { '202': { description: Queued } }
  /reports/utilization:
    get:
      summary: Benefit utilization report
      security: [ { oauth2: [ report.read ] } ]
      parameters:
        - { name: from, in: query, schema: { type: string, format: date } }
        - { name: to, in: query, schema: { type: string, format: date } }
        - { name: benefitCategory, in: query, schema: { type: string } }
      responses: { '200': { description: OK } }
```

---

## 11. Rate Limiting & Headers

- `429` with `Retry-After` when Kong quota exceeded (per provider subscription).
- Response headers: `X-Correlation-Id`, `X-RateLimit-Remaining`, `ETag`.
- Request headers: `Authorization`, `Idempotency-Key` (unsafe ops), `If-Match` (updates), `Accept-Language` (localized notifications/errors: `ar`, `en`).

---

## 12. FHIR R4 Alignment

HBMP resources map to FHIR R4 for interoperability/export. Mapping is at the **API/adapter layer**; internal storage stays relational.

| HBMP entity | FHIR R4 resource | Key mappings |
|---|---|---|
| `beneficiary` (+ identifiers, contacts) | **Patient** | `Patient.identifier` ← beneficiary_identifier (system per type: NationalID/Passport/RefugeeID/UNHCRNo/MemberNo); `name`, `birthDate`, `gender`, `telecom`, `address` |
| `policy` + `coverage` + `coverage_limit` | **Coverage** | `Coverage.beneficiary` → Patient; `payor` → Mersal/sponsor; `class`, `costToBeneficiary`; limits as extensions |
| `investigation_order` / `order_line` | **ServiceRequest** | `ServiceRequest.code` (CPT/LOINC), `quantityQuantity`, `status` (mapped from lifecycle), `subject`, `requester` |
| `order_fulfillment` + result | **DiagnosticReport** / **Observation** | report references ServiceRequest; result document as `presentedForm` |
| `prescription` / `prescription_line` | **MedicationRequest** | `medicationReference` → Medication (drug), `dosageInstruction`, `dispenseRequest.quantity`, `status` |
| `dispense_event` | **MedicationDispense** | `quantity`, `whenHandedOver`, `authorizingPrescription` → MedicationRequest |
| `authorization` / `decision` | **Claim** / **ClaimResponse** or **CoverageEligibilityResponse** | pre-auth semantics |
| `referral` | **ServiceRequest** (`intent=order`, category=referral) | `performer` → to-provider |
| `encounter` | **Encounter** | `class`, `period`, `subject`, `participant` |
| `diagnosis` | **Condition** | `code` (ICD-10/11), `clinicalStatus`, `encounter` |
| `vital` | **Observation** (vital-signs) | `code` (LOINC), `valueQuantity` |
| `allergy` | **AllergyIntolerance** | `code`, `reaction`, `criticality` |
| `provider` / `provider_location` | **Organization** / **Location** | contract terms out of scope for FHIR core |

### 12.1 Status mapping example (ServiceRequest)

| HBMP order status | FHIR `ServiceRequest.status` |
|---|---|
| Requested / PendingApproval | `draft` |
| Approved / Active | `active` |
| PartiallyUsed | `active` |
| Completed | `completed` |
| Rejected / Cancelled | `revoked` |
| Expired | `revoked` |

A read-only FHIR façade (`/fhir/r4/*`) can be layered later; the native `/api/v1` remains primary.

---

## 13. Cross-References

- Resource shapes/keys: [15-database-erd.md](15-database-erd.md)
- Idempotency/consume realization & sagas: [16-service-architecture.md](16-service-architecture.md)
- Status enums & guards: [23-state-machines.md](23-state-machines.md)
- Scope/RLS enforcement: [18-security-model.md](18-security-model.md)
- Column types & validation: [22-data-dictionary.md](22-data-dictionary.md)
