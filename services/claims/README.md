# claims-service (Phase 10b)

Turns already-delivered, authorized services into **reviewed, decided and settled financial records** — three
origination channels (auto-derived, provider-submitted, beneficiary reimbursement), batching, rules-based
pre-adjudication, line-level Claims Officer decisions rolled up to batch, reconciliation + append-only adjustments,
and an immutable settlement advice. **The platform never moves money** — settlement advice is the hand-off to
Finance; there is no payment execution or bank-rail integration anywhere.

Authoritative design: [`36-claims-management.md`](../../HBMP-Design/36-claims-management.md). Schema/enums:
[`22-data-dictionary.md §10A / §11.5`](../../HBMP-Design/22-data-dictionary.md). Lifecycles:
[`23-state-machines.md §7–§10`](../../HBMP-Design/23-state-machines.md). Build prompt:
[`phase-10b-claims-management.md`](../../HBMP-Design/claude-code-prompts/phase-10b-claims-management.md).

Owns schema `claims` exclusively (schema-per-service, RLS on). It never reads another service's tables — cross-context
data (tariffs, authorizations, eligibility, fulfillment) comes over the API/events.

## The invariants (enforced, not commented)

1. **No double-billing — enforced by the database.** `UNIQUE(fulfillment_ref) WHERE fulfillment_ref IS NOT NULL AND
   status <> 'Void'` (`ux_claim_line_fulfillment`) makes a second live payable line for a fulfillment/dispense record
   *impossible*, not merely unlikely. A loser fails on SQLSTATE 23505 and surfaces as `DUPLICATE_CLAIM` (409). Proven
   by a real 8-way parallel-transaction concurrency test.
2. **Idempotent intake.** Auto-derive consumers dedupe on event id (`processed_event`); a redelivered event is a
   no-op returning the prior line.
3. **Never a guessed price.** Pricing comes from `contract_service_line.agreed_price` for the code + service date. No
   tariff ⇒ `NO_TARIFF` ⇒ `RequiresManualReview`, `contract_price` stays null. Never defaulted/averaged/carried-over.
4. **Claims ≠ diagnosis.** The `claims` schema carries **no clinical column anywhere**. Every projection is a
   server-side allow-list DTO (codes + amounts + linkage + statuses). Enforced in three layers: the authz overlay
   grants claims roles no clinical action (default-deny), the DTOs are structurally clinical-free, and the schema has
   no clinical field to source one from. Proven by `ClaimsCannotReadDiagnosisTests`.
5. **Provider isolation.** ABAC provider-ownership + Postgres RLS (`app.tenant_id` / `app.provider_id` GUCs). A
   provider never sees another provider's claims.
6. **Never re-decrement coverage.** Claims read/reconcile against `consumed_value`; they never write it.

## Slices

- **10b.1 (this slice) — foundation + auto-derived claims.** `claim` + `claim_line` (+ `claim_seq`, `processed_event`),
  the no-double-billing unique index, RLS, the idempotent auto-derive intake executor (`ClaimIntakeExecutor`), tariff
  pricing (`IContractTariffProvider` → provider-service), min-necessary reads, and the `/api/v1/claims/intake` seam
  (mirrors finance `/projections` pending the fanout bus). Emits `ClaimCreated` / `ClaimLineCreated` via the outbox.
- **10b.2 — batching + batch lifecycle.** `claim_batch` + `claim_batch_item` (+ `batch_seq`), the single-open-batch
  partial unique index `ux_claim_one_open_batch` (a claim can never sit in two live batches → never settled twice),
  the 23 §9 lifecycle (Open→UnderReview→Decided→SettlementIssued→Closed + Cancelled) with its guards (≥1 claim to
  review, every line decided to Decide, reason to cancel/exception-remove), and rollups recomputed on every change and
  frozen at SettlementIssued. Emits `BatchCreated` / `BatchUnderReview` / `BatchDecided` via the outbox.
- **10b.3 — automated pre-adjudication.** The `Adjudicator` runs the fixed 9-step order (eligibility → coverage →
  pre-auth → fulfillment → duplicate → network → tariff → limit → co-pay) per line, **collects ALL applicable reason
  codes** (never stops at the first failure), and computes `system_recommendation` + `allowed_amount` (capped by
  auth scope and limit remaining, minus member share) + `rule_version`. Hard blocks ⇒ Deny; `NO_TARIFF` ⇒
  RequiresManualReview (price stays null); caps ⇒ Partial. Coverage accumulators are **read, never written**.
  `POST /claims/{id}/adjudicate`; emits `ClaimAdjudicated`; the append-only per-run history is the audit event.
- 10b.4 officer line decisions (SoD + dual control) · 10b.5 provider-submitted · 10b.6 reimbursement + OCR · 10b.7
  reconciliation + adjustments · 10b.8 settlement advice + exports · 10b.9 appeals + KPIs. *(built in subsequent slices)*

## Endpoints (10b.1)

| Method | Path | Scope | Notes |
|---|---|---|---|
| GET | `/api/v1/claims` | `claims:read` | Min-necessary list; provider-isolated; audited read |
| GET | `/api/v1/claims/{id}` | `claims:read` | Min-necessary detail; provider-isolated; audited read |
| POST | `/api/v1/claims/intake` | `claims:ingest` | Auto-derive seam; `Created` / `Replayed` / 409 `DUPLICATE_CLAIM` |

## Local run / tests

```bash
# apply the migration to the dev DB (host Postgres :55432 or the compose postgres)
psql "host=localhost port=55432 dbname=hbmp user=hbmp" -f Infrastructure/Migrations/0001_claims.sql

# unit/authz tests always run; DB integration + concurrency tests are env-gated (need the hbmp superuser conn)
CLAIMS_TEST_DB="Host=localhost;Port=55432;Database=hbmp;Username=hbmp;Password=***" \
  dotnet test services/claims/Tests/Mersal.Claims.Tests.csproj
```

Behind Kong at `/api/v1/claims`; compose service `claims-service` (`:8106` on the host).
