# Phase 18 — Remediation TODO (execution tracker)

Source of truth for findings: [`AUDIT-R2-E2E.md`](AUDIT-R2-E2E.md) · gates: [`../HBMP-Design/claude-code-prompts/phase-18-e2e-remediation.md`](../HBMP-Design/claude-code-prompts/phase-18-e2e-remediation.md)

Mark `✅ YYYY-MM-DD` per id here **and** on the finding row in `AUDIT-R2-E2E.md` as each closes.

## Gate A — Benefit & money correctness (blocks any pilot with real data) — ✅ COMPLETE 2026-07-27

| Sub | Id | Finding | Status |
|---|---|---|---|
| 18.A1 | X1 | `coverage_limit.consumed_value` never incremented → every member eligible forever | ✅ 2026-07-27 |
| 18.A2 | X2 | Batch rollups erase applied adjustments at the freeze point | ✅ 2026-07-27 |
| 18.A2 | X3 | Allowed amount can exceed contract tariff (`Math.Max`; `Adjust` uncapped) | ✅ 2026-07-27 |
| 18.A2 | X8 | Contract price ignores line quantity | ✅ 2026-07-27 |
| 18.A3 | X7 | Lost update on concurrent consume/dispense of different lines | ✅ 2026-07-27 |
| 18.A3 | X9 | Eligibility cache key omits serviceCode/requiresPreAuth → pre-auth bypass | ✅ 2026-07-27 |
| 18.A3 | X10 | First-ever limit reset wipes in-period consumption (test asserts the bug) | ✅ 2026-07-27 |
| 18.A3 | — | Waitlist promotion unlocked; cancel/no-show 3 unwrapped SaveChanges; prefix idempotency keys | ✅ 2026-07-27 |
| 18.A3 | — | `IBusinessCalendar.Today()` Africa/Cairo + TimeProvider injection + UtcNow ban | ✅ 2026-07-27 |
| 18.A4 | F10–F12,F19,F20,F27,F30 | Report-access TTL cap + missing transitions; settlement SoD + frozen snapshot; adjustment TOCTOU; policy reactivation; voided claim terminal; TransitionDenied audit | ✅ 2026-07-27 |
| 18.A4 | — | State-machine conformance test generated from `23-state-machines.md` | ✅ 2026-07-27 |

## Gate B — Security closure (no pilot data before this)

| Sub | Id | Finding | Status |
|---|---|---|---|
| 18.B1 | X4 | MinIO PHI-bucket credential committed in `document/Api/appsettings.json` | ✅ 2026-07-27 (+ WORM bucket, found by the new rule) |
| 18.B1 | X5 | identity m2m secret defaults to a public literal, all scopes, unrotatable | ✅ 2026-07-27 |
| 18.B2 | X6 | RLS deny-all trap armed in claims, callcentre, admin | ☐ |
| 18.B2 | S1 | provider-service still connects as superuser | ☐ |
| 18.B2 | S2 | interop RLS policy is fail-**open**; GUC never bound | ☐ |
| 18.B3 | S3 | Admin privileged groups un-gated at the framework (no MFA on role grants) | ☐ |
| 18.B3 | S4 | CSRF on issuer cookie POSTs incl. `POST /connect/enroll-2fa` | ☐ |
| 18.B3 | S5 | Token scope grant fails open to the user's full entitlement | ☐ |
| 18.B3 | S6 | patient by-id/search returns national ID + contacts, unscoped, unaudited | ☐ |
| 18.B3 | S7 | identity-service missing `UseHbmpTransportSecurity()` | ☐ |
| 18.B3 | S8 | Kong performs no authentication | ☐ |
| 18.B3 | S9 | No rate limiting on `/connect/*` | ☐ |

## Gate C — Last-mile wiring

| Sub | Id | Finding | Status |
|---|---|---|---|
| 18.C1 | W1 | Live sessions expire every 5 minutes (no refresh exchange) | ☐ |
| 18.C1 | W2 | Branch scoping inert end-to-end (no `X-Active-Branch`, Kong CORS) | ☐ |
| 18.C2 | W3 | interop/FHIR has no compose entry and no Kong route | ☐ |
| 18.C2 | W4 | Report-access grant/decision has no UI → sensitive gate is permanent-deny | ☐ |
| 18.C2 | W5 | identity admin has no Kong route; console edits the legacy projection | ☐ |
| 18.C2 | W6 | SPA commented out of compose | ☐ |
| 18.C2 | W7 | FR-BRN-026/027 unbuilt — doctor bookable at an unassigned branch | ☐ |
| 18.C2 | W8 | 10b.6 OCR reimbursement ticked but structurally inert | ☐ |

## Gate D — UX safety

| Sub | Id | Finding | Status |
|---|---|---|---|
| 18.D1 | U1 | Four write flows fail silently and lack idempotency keys | ☐ |
| 18.D1 | U2 | All 4xx collapse into one message on decide/dispense/consume | ☐ |
| 18.D2 | U3 | Call Centre renders every member status as a green "eligible" chip | ☐ |
| 18.D2 | U4 | Navigation vanishes below 760px | ☐ |
| 18.D2 | U5 | App-bar global search is a dead field bound to `/` | ☐ |
| 18.D2 | U7 | Dates/times use browser locale **and** time zone (not Africa/Cairo) | ☐ |
| 18.D2 | U8/U9 | Undefined CSS tokens; brand-teal avatar contrast ~2.2:1 | ☐ |
| 18.D3 | U6/U10 | axe covers 3 of ~45 routes, no AR/RTL, contrast disabled; a11y defect set | ☐ |

## Gate E — CI truth & quality

| Sub | Id | Finding | Status |
|---|---|---|---|
| 18.E1 | Q1 | Route-coverage guard, `IDENTITY_TEST_DB`, identity/interop OpenAPI claimed but not wired | ☐ |
| 18.E1 | Q2 | GitLab/GitHub CI split-brain | ☐ |
| 18.E1 | Q4 | Coverage floor is 55%, documented as 80% | ☐ |
| 18.E2 | Q3 | masterdata: 21 endpoints, 1 test file, no authz suite | ☐ |
| 18.E2 | — | `libs/testing` extraction, gate consolidation, architecture tests, thin suites, 133 `any` | ☐ |

## Gate F — Enhancements

| Sub | Item | Status |
|---|---|---|
| 18.F1 | Property-based executor tests · `Money` type · Stryker mutation testing | ☐ |
| 18.F2 | Command palette · server-side worklist sort/filter/paginate · keyboard mode · offline · telemetry | ☐ |
| 18.F3 | OpenBao dynamic creds · tenant-isolation fuzzing · audit anomaly detection · DAST · SBOM/cosign · Pact | ☐ |
