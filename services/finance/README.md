# finance-service (Phase 10.2)

Cost/utilization read-models, provider settlements, financial summaries, and audited exports — with one **hard,
tested invariant**: **Finance can never read a diagnosis or any clinical detail** (11-permission-matrix §3.2 Finance
clinical row = all ❌; §4 Finance `diagnosis` = denied, `financials` visible, `pii` masked-min). Bounded context
`finance`, schema `finance`.

## Finance ≠ diagnosis — enforced in three layers

1. **Authorization** (`libs/authz/FinancePolicies`): Finance holds only the finance actions; there is **no rule**
   granting Finance any clinical action, so a diagnosis/EMR read is **default-denied** (403, audited).
2. **`FinanceProjection` whitelist** (Domain): every finance DTO implements `IFinanceProjection`; the guard reflects
   over a type graph and **rejects any property whose name matches a clinical token** — a clinical field cannot be
   added to a finance DTO without failing the unit guard.
3. **Read-model**: `utilization_fact` / `settlement_line` carry billing codes + quantities + amounts only. Facts are
   built from domain events, **never by joining clinical tables**; the projector reads a whitelist of keys and
   **ignores any clinical key** that appears on a source event (the projection boundary).

The required authorization test **`FinanceCannotReadDiagnosisTests`** proves all three: no projection type exposes a
clinical field; the guard catches a hypothetical leak; a Finance principal calling `emr:read` / `emr:read-oversight`
is denied + audited; Finance may still read its own zone.

## Read-models

- **`utilization_fact`** — `beneficiary_id` (masked-min in projections), `coverage_category`, `service_code`
  (CPT/LOINC/ATC **billing** code — never a diagnosis), `provider_id`, `authorized_qty`, `delivered_qty`,
  `unit_cost`, `line_cost`, `period`. Built from `OrderLineConsumed` / `RxDispensed` / `ServiceValued`.
- **`settlement` / `settlement_line`** — per-provider, per-period priced roll-up. Prices come from the
  provider-service `provider_contract` / `contract_service_line` agreed prices (22 §5.3) — **READ via
  `IContractPriceProvider`, never duplicated or mutated here**. A code with no agreed price falls back to the
  observed unit cost (never fabricated).

## APIs (`/api/v1/finance`)

| Method | Route | Action | Notes |
|--------|-------|--------|-------|
| GET | `/utilization` | `finance:read-utilization` | Authorized-vs-delivered + spend; filter period/category/provider/beneficiary. **No clinical filter or column.** |
| GET | `/summaries` | `finance:read-summary` | Spend/qty roll-up by service-line / category / provider. |
| POST | `/settlements` | `finance:generate-settlement` | Generate for provider+period from `utilization_fact` × contract price. |
| GET | `/settlements` · `/{id}` | `finance:read-settlement` | List / detail with priced lines. |
| POST | `/settlements/{id}/submit` | `finance:submit-settlement` | Draft → Submitted (SoD: initiator). |
| POST | `/settlements/{id}/approve` | `finance:approve-settlement` | Submitted → Approved; **approver ≠ submitter → 409 SoD**. |
| POST | `/exports` | `finance:export` | CSV export; masked PII; **high-severity `data.export`** audit (actor, filter, row count, correlation id). |
| POST | `/projections` | `finance:project` | System projection seam (deferred fanout bus). |

Settlement approval publishes `SettlementApproved` (outbox → `finance.events`). **No payment is executed** — `Paid`
is a recorded outcome, not a money movement.

## Data & integrity

Migration `0001_finance.sql`: `utilization_fact`, `settlement` (+ CHECK: an Approved settlement must have distinct
submitter/approver — SoD defense-in-depth), `settlement_line`, `settlement_seq` (`STL-YYYY-NNNNNN`),
`processed_event` (idempotent projection), `export_record`. `xmin` optimistic concurrency on settlements.

## Tests

- `FinanceCannotReadDiagnosisTests` — **the required invariant proof** (guard + authz deny + zone allow + schema).
- `FinanceDomainTests` — settlement numbering, read-not-owned price book, masked CSV export.
- `FinanceIntegrationTests` — env-gated `FINANCE_TEST_DB` (hbmp superuser conn): projector ignores clinical keys,
  settlement priced from the contract book with correct totals, utilization aggregation. Serialized via `finance-db`.

## ADR — why finance is a projection, not a clinical query

Rather than reading clinical/order tables and filtering diagnoses out, finance builds an **event-projected read-model
that only ever carries billing codes + amounts**, and all DTOs derive from a **whitelist** that is structurally
incapable of carrying a clinical field. "Finance ≠ diagnosis" is therefore a property of the type system + the authz
bundle, not of query discipline — the same posture as reporting's de-identified read-model. Contract prices are
**read** from the owning service so there is one source of truth for agreed pricing.
