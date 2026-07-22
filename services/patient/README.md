# patient-service

Phase 1. Owns the `patient` schema: beneficiary, identifiers, contacts, family/dependents (15-database-erd §4). Registration → Pending; activation (Member No) is 1.4.

## 1.1 delivered
- Entities + `0001_patient_schema.sql` (soft-delete + history trigger; **dedup partial unique index** `WHERE is_deleted=false`; Member-No per-year sequence).
- Registration rules (`BeneficiaryRegistrar`, US-001): require name + ≥1 **valid** identifier; **duplicate active identifier → 409 naming the existing record, no second row**; created **Pending**.
- Identifier formats (`IdentifierValidation`) for NationalID/Passport/RefugeeID/UNHCRNo/MemberNo; normalized so trivial variants collide.
- Lifecycle state machine (`BeneficiaryLifecycle`, 23-state-machines §1): legal transitions + mandatory-reason on suspend/block/expire/inactivate.
- APIs: `POST /api/v1/beneficiaries` (Idempotency-Key, 201/409/400), `GET` search (identifier/name/status), `GET /{id}` (min-necessary DTO). Audit on create; `BeneficiaryRegistered` via outbox.

## Tests (16, green offline)
Registration (valid→Pending, duplicate→existing id, normalization-insensitive dedup, missing-field list, bad-format reject) + lifecycle legality/mandatory-reason + Member-No format.

## Remaining in Phase 1
1.2 policy-service · 1.3 document-service · 1.4 registration workflow → activate (issues MRS-M-*, emits BeneficiaryActivated). PATCH (If-Match→412) + status-transition endpoints land with 1.4.
