# patient-service

Phase 1. Owns the `patient` schema: beneficiary, identifiers, contacts, family/dependents (15-database-erd §4). Registration → Pending; activation (Member No) is 1.4.

## 1.1 delivered
- Entities + `0001_patient_schema.sql` (soft-delete + history trigger; **dedup partial unique index** `WHERE is_deleted=false`; Member-No per-year sequence).
- Registration rules (`BeneficiaryRegistrar`, US-001): require name + ≥1 **valid** identifier; **duplicate active identifier → 409 naming the existing record, no second row**; created **Pending**.
- Identifier formats (`IdentifierValidation`) for NationalID/Passport/RefugeeID/UNHCRNo/MemberNo; normalized so trivial variants collide.
- Lifecycle state machine (`BeneficiaryLifecycle`, 23-state-machines §1): legal transitions + mandatory-reason on suspend/block/expire/inactivate.
- APIs: `POST /api/v1/beneficiaries` (Idempotency-Key, 201/409/400), `GET` search (identifier/name/status), `GET /{id}` (min-necessary DTO). Audit on create; `BeneficiaryRegistered` via outbox.
- **Registration is idempotent (migration 0008).** The `Idempotency-Key` header was REQUIRED from phase 3 and
  then discarded — nothing stored it and nothing read it — so a retry after a dropped response, a
  double-submitted form or a client reconnect registered a SECOND PERSON. The duplicate-identifier check is
  not a substitute: it fires only when a card or a national id was entered, and registration accepts a person
  with neither. `patient.processed_request` now records the key, what it produced, and a hash of the request
  (`IdempotencyKeyRules`); a retry returns **200** with the first beneficiary, and a key reused for a
  DIFFERENT person is **422 `idempotency-key-reuse`** rather than a 201 naming somebody else.

## Tests (16, green offline)
Registration (valid→Pending, duplicate→existing id, normalization-insensitive dedup, missing-field list, bad-format reject) + lifecycle legality/mandatory-reason + Member-No format.

## Remaining in Phase 1
1.2 policy-service · 1.3 document-service · 1.4 registration workflow → activate (issues MRS-M-*, emits BeneficiaryActivated). PATCH (If-Match→412) + status-transition endpoints land with 1.4.
