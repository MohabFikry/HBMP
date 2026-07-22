---
name: Beneficiary Lifecycle Management
description: Encodes Mersal beneficiary/member identity, registration→activation, and the canonical status lifecycle plus dependents, documents, and eligibility snapshots. Use when building or reviewing anything that creates, matches, de-duplicates, activates, suspends, or reads beneficiary records, identifiers, family groups, or member status.
---

# Beneficiary Lifecycle Management

## Purpose
Make every beneficiary-facing feature respect Mersal's document-flexible identity model and the canonical member lifecycle. A beneficiary is a refugee/member whose identity can be established from any accepted document, deduplicated to one immutable record, activated against a policy, and then moved through a strict status machine — with full audit and minimum-necessary exposure.

## When to use / when not to use
- **Use when:** designing/coding registration, identity capture, de-duplication/matching, activation, status transitions (suspend/expire/block/reactivate), dependents/family groups, contacts, beneficiary documents, or the eligibility snapshot tied to a beneficiary; reviewing any screen or API that reads or mutates `beneficiary`, `beneficiary_identifier`, `contact`, `family_group`, `dependent_link`.
- **Not for:** coverage/limit math and eligibility computation internals (touch only the snapshot here), clinical/EMR records, or provider onboarding. Defer schema mechanics to Healthcare Database Architect and platform seams to the Platform Architect.

## Mersal domain knowledge & rules
- **Identity is document-flexible.** A beneficiary may be identified by any of `NationalID`, `Passport`, `RefugeeID`, `UNHCRNo`, `MemberNo`. Internally every beneficiary has ONE immutable surrogate `beneficiary_id` (UUID v7) and 0..n `beneficiary_identifier` rows (type + value + issuing authority + validity window + `is_primary`). **No dead-ends when documents are incomplete** — capture what exists, activate, complete later.
- **Member business key** `member_no` = `MRS-M-YYYY-NNNNNN` (regex `^MRS-M-\d{4}-\d{6}$`), unique where `is_deleted=false`.
- **De-duplication is mandatory before create.** Match across identifiers (strong match on National ID / Passport / Refugee ID / UNHCR No, with typo tolerance) to resolve "new vs returning"; record the dedup/merge decision in audit.
- **Identifier uniqueness:** `UNIQUE(identifier_type, identifier_value) WHERE is_deleted=false`; one primary identifier per type.
- **Registration → activation flow:** enroll → `Pending` (`MemberCreated`, dedup recorded) → activate only when **documents verified AND policy bound** → `Active` (`MemberActivated`, issue card, verification evidence linked). Abandonment with no docs in window auto-times-out to `Inactive`.
- **Dependents & family:** `family_group` (with `family_code`, head) + `dependent_link` (guardian↔dependent, `relationship` ∈ Child/Spouse/Parent/Other). Model many-to-many via `dependent_link` — a person can be a dependent in one link and guardian in another; never duplicate beneficiary rows.
- **Contacts** are normalized 1:N; `preferred_channel` (SMS/Email/Push) drives notification routing (launch = in-app + email; SMS/WhatsApp future).
- **Minimum-necessary exposure:** most fields here are PII; identifier *values* are **SPI** (refugee/legal status — strictest access, redacted by default). Downstream provider queues see only minimum beneficiary identity. Every read of clinical-linked data is audited.
- **Eligibility snapshot** (`eligibility_snapshot`) is a derived, cached materialization keyed `(beneficiary_id, coverage_id)` with `decision` ∈ Eligible/Ineligible/NeedsAuthorization, `expires_at`, `version_hash`; it is invalidated by `BeneficiaryStatusChanged`, coverage, and consume/dispense events — never the source of truth.
- **Events:** patient-service publishes `BeneficiaryRegistered` (seeds EMR/policy/reporting read refs) and `BeneficiaryStatusChanged` (drives suspend/expire cascades in policy + eligibility).

## Key entities, states & invariants
Canonical member status (from `../../23-state-machines.md` §1): **`Pending → Active → (Suspended | Expired | Blocked | Inactive)`** with re-entry paths:
- `Pending → Active` (activate) or `→ Inactive` (abandon/withdraw).
- `Active → Suspended` (non-payment/review, reason mandatory), `→ Expired` (policy end), `→ Blocked` (fraud/abuse confirmed, justification mandatory, Super Admin/Director), `→ Inactive` (voluntary).
- Reinstate `Suspended → Active`; renew `Expired → Active`; unblock `Blocked → Active` (case review); reactivate `Inactive → Active`.
- Invariants: `beneficiary_id` is immutable; every transition writes an append-only audit event (actor, from/to, reason where required); illegal transitions rejected as `TransitionDenied`; soft-delete + `beneficiary_history` (jsonb snapshot, system-time `valid_from`/`valid_to`) preserves point-in-time state; `row_version` for optimistic concurrency.

## How to apply
- Gate any "create beneficiary" path behind a de-duplication check; surface disambiguation with minimum-necessary candidate data and audit the resolution.
- Never block registration on missing documents; allow `Pending` with partial identifiers and drive activation from the verified-docs + bound-policy guard.
- Enforce the status machine centrally; reject direct status writes that skip guards; require reasons on Suspend/Block/Reject transitions.
- Treat identifier values as SPI: mask/redact by default, never place in logs, search indexes, or notifications; expose to provider queues only as minimum identity.
- When status changes, ensure `BeneficiaryStatusChanged` is emitted so policy/eligibility cascade and caches invalidate.
- In reviews, flag: mutable/re-issued `beneficiary_id`, duplicate records, unaudited merges, PII/SPI over-exposure, status transitions bypassing the machine.

## Canonical references
- Product/identity framing: `../../01-product-vision.md`; foundations: `../../0A-DESIGN-FOUNDATIONS.md`
- Structure, keys, soft-delete/history: `../../15-database-erd.md` §4
- Field types, PII/SPI classification, validation, enums: `../../22-data-dictionary.md` §2, §11
- Member state machine & guards: `../../23-state-machines.md` §1

## Guardrails
- One immutable `beneficiary_id` per person; identity is many identifiers, one record — never collapse multiple people or split one.
- Registration must never dead-end on incomplete documents.
- All status changes go through the canonical machine with audit + mandatory reasons where specified.
- Identifier values are SPI: strictest access, redacted by default, excluded from search/exports/logs.
- The eligibility snapshot is derived and cacheable — never treat it as the authoritative source of coverage.
