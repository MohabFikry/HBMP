# Finance & the pharmacy counter — design spec

**Date:** 2026-08-12
**Design doc:** `HBMP-Design/49-finance-and-the-counter.md`
**Branch:** `feat/finance-and-pharmacy-counter` (stacked on `feat/approvals-claims-workbench`)

---

## 1. What this is

The fourth pass of the client-vs-service audit. Passes one to three covered the clinic-management, medical
director, approvals and claims portals. This one covers **finance** (`finance` role) and the **pharmacy
counter** (`pharmacy` role).

The two portals are unequal in size. Finance has seven defects, two of which mean a screen has never worked
outside a fixture. Pharmacy has one, and it is a feature that exists in the contract, in the screen, in the
dev fixtures and in the tests — and nowhere else.

---

## 2. The findings

### Finance

| # | Finding | Evidence |
|---|---|---|
| F1 | `settlements()` and `exportReport()` throw on **every** call: they map `status: "ok"` (a string) into `zStatus` (`{kind,label}`), and `parseOr` throws | proven by running `zSettlement.safeParse` directly |
| F2 | The export **downloads nothing** — server returns `text/csv` via `Results.File`, client calls `postRaw` and reads a JSON row count | `FinanceEndpoints.cs:190`, `HttpApiClient.exportReport` |
| F3 | The export **ignores `report`** — always runs `UtilizationAsync`; `report` names only the file and the audit event | `FinanceEndpoints.cs:170` |
| F4 | **XLSX is never produced**, and the claimed format is stored in `ExportRecord` | `FinanceEndpoints.cs:177,190` |
| F5 | Generate / submit / approve have **no affordance**; `finance` holds `finance:write` and `finance:approve` | `catalog.ts` finance sections; `FinancePortal.tsx` |
| F6 | All four settlement states render an **identical chip** — `status` hardcoded, real value in `state`, never shown | `HttpApiClient.settlements` |
| F7 | **No period control** — `/utilization` and `/summaries` accept `from`/`to`; neither screen sends them | `FinancePortal.tsx` |
| F8 | `/settlements` `providerId` and `status` unused; server caps at 100 silently | `FinanceEndpoints.cs:101` |
| F9 | `SettlementLineView.PriceSource` (`Contract` \| `ObservedFloor`) is projected and **dropped by the client** | `Projections.cs:44`, `HttpApiClient.settlements` |

### Pharmacy

| # | Finding | Evidence |
|---|---|---|
| P1 | `POST /{rxId}/lines/{lineId}/out-of-stock` is **unreached by the SPA** | grep across `apps/web/src` |
| P2 | The server's `DispensableLineView` **does not carry** `outOfStock`; `HttpApiClient` hardcodes `false`; `DevApiClient` hardcodes `true` on one fixture | `Contracts.cs:209`, `HttpApiClient.ts:2056`, `DevApiClient.ts:2385` |
| P3 | The endpoint **stores nothing**, so the flag cannot survive a reload, re-flagging re-notifies, and "what are we out of" is unanswerable | `Dispensing.cs:343` |

### Carried along, not audited

`caseTasks()` and `escalations()` have the same `status: "ok"` crash. They belong to the case portal. Fixed
because they are one line each and proven broken; flagged so nobody reads them as reviewed.

### Deliberately out of scope

`POST /{rxId}/lines/{lineId}/amend-schedule` and `POST /{rxId}/cancel-lines` are also unreached, but they
carry `rx:write` — the prescriber's authority, not the counter's. They belong to a prescribing-portal pass.

---

## 3. Decisions

Four questions, four answers.

1. **Finance write surface → the full settlement lifecycle.** Generate, submit and approve, with the SoD
   refusal rendered as its own sentence, real state chips, and the row cap surfaced. No new scope is granted.
2. **Exports → honest download, honest formats.** A real file download; the server honours `report` and
   produces real settlement and summary CSVs; XLSX is removed rather than shipped as a control that
   substitutes another format; the `category` and `providerId` filters the endpoint already accepts are sent.
3. **Out of stock → persisted on the line.** Who, when, how much, and the note. Idempotent re-flagging (no
   second notification), cleared on dispense.
4. **Finance period → `PeriodControl`, server-side**, shared across Utilization, Summaries and Exports.
   Settlements is excluded on purpose — see §4.4.

---

## 4. The design

### 4.1 Server — finance

**`Projections.cs`** — `SettlementView` gains `SubmittedBy` and `ApprovedBy` (staff subject ids, the same
class of identifier `WorklistItemView.AssignedReviewerId` carries). This is what lets the screen honour SoD
before the click instead of after the 409.

**`FinanceQueries.cs`** — two new CSV renderers beside the existing utilization one:
`ToCsv(IReadOnlyList<SettlementView>)` (one row per settlement **line**, carrying the settlement number,
provider ref, period, status and price source) and `ToCsv(FinancialSummaryView)`.

**`FinanceEndpoints.cs`**

- `POST /exports` switches on `report`: `utilization` | `settlement` | `summary`. An unknown report is 422
  `unknown-report` rather than a silent fallback. A `format` other than `csv` is 422 `unsupported-format` —
  the endpoint stops recording a format it did not produce.
- The row count moves onto an `X-Row-Count` response header so a client receiving a file still gets the
  audited figure. Kong must expose it (see §4.5).
- `GET /settlements` sets `X-Total-Count` and keeps its 100-row cap, which the screen now states.

No migration. `SubmittedBy`/`ApprovedBy` are existing columns.

### 4.2 Server — pharmacy

**Migration `0020_prescription_line_out_of_stock.sql`** adds four columns to
`pharmacy.prescription_line`: `out_of_stock_at timestamptz`, `out_of_stock_by text`,
`out_of_stock_qty numeric`, `out_of_stock_note text`; a CHECK that the timestamp and the actor are present
together or both absent; and a partial index on the flagged rows.

Idempotent under re-application — every migration in this repository re-runs on every pass.

**`Dispensing.cs`**

- `DispensableLineView` gains `OutOfStock`, `OutOfStockAt`, `OutOfStockNote`.
- The out-of-stock handler persists inside a transaction, and **returns early without notifying** when the
  line is already flagged — the replay answers with what was recorded.
- The dispense handler clears the flag when a quantity is dispensed against a flagged line.

The accumulator is untouched in both directions. Out of stock is a fact about the pharmacy.

### 4.3 Contracts and client

`libs/contracts/src/finance.ts`

- `zSettlementLine` gains `priceSource: z.enum(["Contract","ObservedFloor"])`.
- `zSettlement` gains `submittedBy` / `approvedBy` (nullish).
- `zExportRequest` drops `xlsx`, gains optional `category` and `providerId`.
- New `zGenerateSettlementRequest`.

`libs/contracts/src/pharmacy.ts` — `zPrescriptionLine` gains `outOfStockAt` and `outOfStockNote` beside the
existing `outOfStock`, so the chip can say *when* rather than only *that*.

`apps/web/src/api/`

- `http.ts` gains a `postForFile` returning `{ blob, filename, rowCount }` parsed from
  `Content-Disposition` and `X-Row-Count`.
- `HttpApiClient`: `settlementChip(state)` replaces the literal; `utilization(period)`,
  `financialSummary(dimension, period)`, `settlements(filter)` returning `{rows,total}`;
  `generateSettlement` / `submitSettlement` / `approveSettlement`; `exportReport` returning a file;
  `flagOutOfStock`; `outOfStock` read from the server instead of `false`.
- `DevApiClient` and `client.ts` follow, with fixtures that use the real vocabulary.

### 4.4 Screens

**`FinancePortal.tsx`**

- *Utilization* and *Summaries* take `PeriodControl` (storage key `finance-period`).
- *Settlements* gains a generate form (provider + period), Submit and Approve, a real four-state chip, a
  `priceSource` column with an unpriced-line count in the header, a server-side status filter, and the
  truncation banner. Approve is unavailable to the submitter, with the reason written out.
- *Exports* downloads a file, drops the XLSX segment, and shares the portal period.

**Why Settlements has no period control.** A settlement carries its own period as columns. Filtering a list
of period-stamped rows by a global period means either containment or overlap; they differ exactly on the
boundary-spanning settlements a query is most likely to be about. Picking one silently would be a filter
that means something the operator did not ask for.

**`pharmacy/PrescriptionPage.tsx`** — an "Out of stock" control per fillable line, opening a small form for
an optional quantity and note; the flagged line shows who and when; the control is absent once flagged.

### 4.5 Gateway

`X-Row-Count` is added to Kong's `exposed_headers`. A response header is invisible to cross-origin JavaScript
unless the gateway lists it — the same trap `X-Active-Branch` documents in that file, and the one
`X-Total-Count` hit in the previous pass.

---

## 5. Testing

**`services/finance/Tests/`** — export report selection (three reports produce three different headers), 422
on an unknown report, 422 on a non-CSV format, `X-Row-Count` matches the rows, the settlement lifecycle
end to end, SoD refusal on self-approval, `SubmittedBy` present on the view.

**`services/pharmacy/Tests/`** — flag persists and is reported on the view; re-flagging is idempotent and
does not enqueue a second notification; dispensing clears it; the accumulator is unchanged by flagging.

**`apps/web/test/`** — a new `http-client-contract.test.ts` that runs `HttpApiClient`'s mappings against the
real schemas over a stubbed `fetch`. This is the test whose absence is finding F1, and it is the one that
matters most: without it the next literal `status: "ok"` ships the same way.

Plus screen tests for the settlement lifecycle, the SoD sentence, the price-source column, the export
download and the out-of-stock control.

---

## 6. Risks

- **`X-Row-Count` unexposed in a deployment whose Kong config lags.** Degrades to a missing count, not a
  missing file. The download is independent of the header.
- **Settlement CSV size.** Bounded by the same 100-settlement cap as the list.
- **The new HTTP-client contract test is broad.** It will fail loudly on any future mapping drift, which is
  the point, but it means a contract change now touches one more file.

---

## 7. Verification

### The two headline defects, reproduced before they were fixed

Neither was inferred from reading. Both were run.

**F1 — the schema crash.** `zSettlement.safeParse` was executed directly against the payload the client
built:

```
status "ok": {"success":false,"error":{"issues":[
  {"code":"invalid_type","expected":"object","received":"string","path":["status"]}]}}
```

And once the fix and its test were both in place, the original literal was **put back** to confirm the new
test catches it rather than merely passing beside it:

```
× parses a settlement list, which it could not do at all before
  → Response failed contract validation: Expected object, received string
× gives each settlement state its own chip …          → same
× keeps the price source …                            → same
× carries submittedBy …                               → same
```

Four failures, the exact production error. The literal was then restored to the fix.

**P1 — out of stock existed only in a fixture.** Established by grep across all five layers: the contract
declares it, the screen renders it, the server's `DispensableLineView` does not carry it, `HttpApiClient`
writes `false`, `DevApiClient` writes `true` on one row.

### Test results

| Suite | Result |
|---|---|
| `apps/web` full suite | **116 files / 1487 tests, 0 failures** — includes axe over every route × locale × theme |
| `test/http-client-contract.test.ts` (new) | 14/14 |
| `test/finance-and-counter.test.tsx` (new) | 11/11 |
| `test/prescription-page.test.tsx` (+5) | 40/40 |
| `Mersal.Finance.Tests` (`--with-db`) | **31/31** |
| `Mersal.Pharmacy.Tests` (`--with-db`) | **182/182** |
| `tsc --noEmit` (apps/web) | clean |

### Gates

| Gate | Result |
|---|---|
| OpenAPI drift | ✓ after `--fix`; the diff is **24 added lines, additive only** — the six fields this pass introduced |
| Kong route coverage | ✓ every served public resource has a route |
| migration-compat | ✓ no unacknowledged contract-phase operations |
| SPA scope guard | ✓ `config.ts` matches `IdentityContract` |
| invariant registry | ✓ every named test exists and runs |
| live-bundle clean | ✓ no fixture marker survives a `VITE_LIVE=1` build |
| design guards | ✓ |
| response schemas | ✓ no service describes less than it did |
| service inventory | ✓ 22 services |
| button icon policy | ✓ *after a fix* — see §8 |
| gate freshness | **cannot run locally.** It reads a CI-only `.ci-state` heartbeat ledger that is never committed; every gate reports "never recorded" on any developer machine. Not introduced here. |

### The migration replays

`tools/ci/apply-migrations.sh` halts before `pharmacy/` on the pre-existing `admin/0007` failure
(`23505 ux_bsg_home_per_subject`) — PR #7's bug, on PR #7's branch, proved by file substitution in the
previous pass. `0020` was therefore applied directly, twice in succession, both clean:

```
=== pass 1 ===  exit=0
=== pass 2 (re-runnability) ===  exit=0
```

---

## 8. Revised during implementation

Seven departures from §4, all recorded because a spec that only describes what went to plan is not a record.

1. **`GET /{id}/dispensing` needs `provider:read`, not just `pharmacy:*`.** The first counter-side test 403'd
   on the dispensing VIEW after the POST already worked: the read is gated on the provider-queue rule.
   Identity 0005 does grant a `pharmacist` that scope, so the fixture was under-scoped, not the product.
   The test client now carries exactly the issuer's set — *a fixture more generous than the issuer tests a
   system nobody runs; one meaner fails on a rule that would never fire.*

2. **`PrescribingTestAuth` had no provider claim.** `DispensingGate` refuses a caller with no provider before
   consulting any policy, so a counter test omitting it gets a 403 that says nothing about the rule it meant
   to exercise. Added `X-Test-Provider`.

3. **The role is `pharmacist`, not `pharmacy`.** The scope catalogue's domain is `pharmacy`; the role is not.

4. **One new test passed vacuously.** `Reporting_a_shortage_consumes_nothing` asserted only that quantities
   were unchanged — trivially true when the request 403s, so it reported a pass while the other three failed.
   It now asserts the 202 and the flag first.

5. **Explicit transaction on the out-of-stock write.** §4.2 said "persists inside a transaction" loosely;
   `EfOutbox` shares the handler's context, so the flag and its event would already have committed together.
   Made explicit anyway, because the idempotency guard changes the calculus: a crash between the flag landing
   and the event being staged would leave a line marked short, a counter shown a chip, and a prescriber never
   told — and the retry, finding the flag set, would replay and notify nobody. **Permanent and silent.**

6. **`ExportRequest` gained `Dimension`.** Not in §4.1. Without it a summary export always grouped by service
   line, so exporting the Summaries screen while it showed "by provider" produced a different roll-up under
   the same name — a quieter version of the defect being fixed.

7. **Two obsolete tests rewritten, not deleted.** `finance-export.test.tsx` asserted the free From/To inputs
   and the "From must be on or before To" alert, both replaced by `PeriodControl`. The window assertion moved
   to the presets. The validation assertion became an **impossibility** assertion: a preset resolves to an
   ordered window, so the invalid state has no representation. That is the one case where losing a validation
   test is a gain, and it is asserted rather than assumed.

8. **The button-icon policy caught two bare buttons.** `Submit for approval` and `Generate draft` are members
   of recurring action classes the product gives glyphs (`check2`, `plus`). A real consistency rule, found by
   its gate rather than by review.
