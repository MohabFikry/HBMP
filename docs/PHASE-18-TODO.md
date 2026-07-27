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
| 18.B2 | X6 | RLS deny-all trap armed in claims, callcentre, admin | ✅ 2026-07-27 |
| 18.B2 | S1 | provider-service still connects as superuser | ✅ 2026-07-27 (all 7 superuser connections flipped) |
| 18.B2 | S2 | interop RLS policy is fail-**open**; GUC never bound | ✅ 2026-07-27 (+ admin's 4 migrations, same shape, not in the audit) |
| 18.B2 | — | *(found)* eligibility stamped a hardcoded `SoleTenantId` on the projection write path | ✅ 2026-07-27 (tenant from envelope; 28 publishers enriched) |
| 18.B2 | — | *(found)* admin write paths silently substituted the caller's tenant for a mismatched body tenant | ✅ 2026-07-27 (403, not a redirected write) |
| 18.B3 | S3 | Admin privileged groups un-gated at the framework (no MFA on role grants) | ✅ 2026-07-27 (route-table gate, not a handler convention) |
| 18.B3 | S4 | CSRF on issuer cookie POSTs incl. `POST /connect/enroll-2fa` | ✅ 2026-07-27 (antiforgery on all 3 forms + Secure/SameSite=Strict cookies) |
| 18.B3 | S5 | Token scope grant fails open to the user's full entitlement | ✅ 2026-07-27 (+ found: the intersection dropped offline_access → no refresh token ever) |
| 18.B3 | S6 | patient by-id/search returns national ID + contacts, unscoped, unaudited | ✅ 2026-07-27 (patient:read split, engine+tenant+PHI-read audit+pii/contact projection) |
| 18.B3 | S7 | identity-service missing `UseHbmpTransportSecurity()` | ✅ 2026-07-27 |
| 18.B3 | S8 | Kong performs no authentication | ✅ 2026-07-27 (Kong OSS jwt plugin + registered issuer key; 🟡 needs a live stack to verify) |
| 18.B3 | S9 | No rate limiting on `/connect/*` | ✅ 2026-07-27 (10/min credential, 60/min token, per-IP) |
| 18.B3 | — | *(found)* 141 rule/role pairs no token could satisfy — policy bundles vs the identity seed | ✅ 2026-07-27 (135 closed by 0004+0005; 6 declared) |
| 18.B3 | — | **OPEN — needs a product decision**: 6 roles named by policy rules but absent from the frozen role vocabulary | ☐ claims_reviewer, manager, network_manager, approvals_team, finance_approver, call_center_supervisor |

## Gate C — Last-mile wiring

| Sub | Id | Finding | Status |
|---|---|---|---|
| 18.C1 | W1 | Live sessions expire every 5 minutes (no refresh exchange) | ✅ 2026-07-27 (refresh + rotation + single-flight + silent renew) |
| 18.C1 | W2 | Branch scoping inert end-to-end (no `X-Active-Branch`, Kong CORS) | ✅ 2026-07-27 (header on every request, Kong CORS both lists, server echo) |
| 18.C2 | W3 | interop/FHIR has no compose entry and no Kong route | ✅ 2026-07-27 (compose + Kong /fhir,/interop; metadata stays public) |
| 18.C2 | W4 | Report-access grant/decision has no UI → sensitive gate is permanent-deny | ✅ 2026-07-27 (inbox endpoint + approver screen on both portals + hourly expiry sweeper) |
| 18.C2 | W5 | identity admin has no Kong route; console edits the legacy projection | ✅ 2026-07-27 (/identity routed; console reads the real store incl. 2FA state) |
| 18.C2 | W6 | SPA commented out of compose | ✅ 2026-07-27 |
| 18.C2 | W7 | FR-BRN-026/027 unbuilt — doctor bookable at an unassigned branch | ✅ 2026-07-27 (IPractitionerBranchDirectory; 422 at BOTH availability + booking) |
| 18.C2 | W8 | 10b.6 OCR reimbursement ticked but structurally inert | ✅ 2026-07-27 (10b.6 downgraded to ◐ — the honest option; seams named) |

## Gate D — UX safety

| Sub | Id | Finding | Status |
|---|---|---|---|
| 18.D1 | U1 | Four write flows fail silently and lack idempotency keys | ✅ 2026-07-27 (useWrite: per-form key + typed alert on all four) |
| 18.D1 | U2 | All 4xx collapse into one message on decide/dispense/consume | ✅ 2026-07-27 (one writeErrorMessage: 401/403/404/409/412/422/429/5xx + network/schema) |
| 18.D2 | U3 | Call Centre renders every member status as a green "eligible" chip | ✅ 2026-07-27 (chip kind + label from the SAME value; unknown → neutral) |
| 18.D2 | U4 | Navigation vanishes below 760px | ✅ 2026-07-27 (bottom tab bar ≥44px, safe-area aware, RTL-logical) |
| 18.D2 | U5 | App-bar global search is a dead field bound to `/` | ✅ 2026-07-27 (dead input removed → 18.F2 palette; visible app-bar affordance restored as a BUTTON that opens it, `/` rebound to the palette) |
| 18.D2 | U7 | Dates/times use browser locale **and** time zone (not Africa/Cairo) | ✅ 2026-07-27 (useFormat: Africa/Cairo + app locale; money → raw numbers; ESLint ban) |
| 18.D2 | U8/U9 | Undefined CSS tokens; brand-teal avatar contrast ~2.2:1 | ✅ 2026-07-27 (tokens renamed + legacy hex dropped + test asserts every var() resolves; avatar --brand → --accent) |
| 18.D3 | U6/U10 | axe covers 3 of ~45 routes, no AR/RTL, contrast disabled; a11y defect set | ✅ 2026-07-27 (route-wide axe in both locales + themes, Playwright contrast job, 8 structural fixes) |

## Gate E — CI truth & quality

| Sub | Id | Finding | Status |
|---|---|---|---|
| 18.E1 | Q1 | Route-coverage guard, `IDENTITY_TEST_DB`, identity/interop OpenAPI claimed but not wired | ✅ 2026-07-27 (Kong guard wired + extended to all public prefixes; CI split-brain resolved, ADR-0001 amended) |
| 18.E1 | Q2 | GitLab/GitHub CI split-brain | ✅ 2026-07-27 (IDENTITY_TEST_DB + Identity/Interop OpenAPI + specs committed + drift check; 2 silent-pass tests → SkippableFact) |
| 18.E1 | Q4 | Coverage floor is 55%, documented as 80% | ✅ 2026-07-27 (domain floor 55→58 with a ratchet + target date; overall coverage now gated too) |
| 18.E2 | Q3 | masterdata: 21 endpoints, 1 test file, no authz suite | ◐ 2026-07-27 — architecture tests + libs/data tests + masterdata authz + cleanups DONE; 3 refactors deferred (see note) |
| 18.E2 | — | `libs/testing` extraction, gate consolidation, architecture tests, thin suites, 133 `any` | ◐ 2026-07-27 — architecture tests + libs/data tests + masterdata authz + cleanups DONE; 3 refactors deferred (see note) |

## Gate F — Enhancements

| Sub | Item | Status |
|---|---|---|
| 18.F1 | Property-based executor tests · `Money` type · Stryker mutation testing | ☑ executor tests + Money landed; Stryker deferred (config-only value without a CI run) |
| 18.F2 | Command palette · server-side worklist sort/filter/paginate · keyboard mode · offline · telemetry | ◐ palette + `g q` fix landed; rest deferred (see note below) |
| 18.F3 | OpenBao dynamic creds · tenant-isolation fuzzing · audit anomaly detection · DAST · SBOM/cosign · Pact | ◐ tenant-isolation fuzzer landed (found a real disclosure); infra items deferred (see note below) |

### 18.E2 — deferred, with reasons

Three items are **not** done and are deliberately left rather than half-done:

| Item | Why deferred |
|---|---|
| `libs/testing` extraction (HbmpDbFixture / RlsIsolationTheory / AuthedClientFactory) + refactor 13 RlsIsolationTests onto it | A pure-refactor of 13 passing safety-critical suites. The duplication is real, but these are the tests that prove tenant isolation — rewriting all of them at once trades a known-good state for a cosmetic gain, and any mistake is silent (a fixture that binds the wrong GUC makes every suite pass). Worth doing as its own reviewable change with the suites green on both sides. |
| Consolidate 16 `*Gate.cs` into `libs/authz` `HbmpGate<TPolicy>` | Same shape, larger blast radius: the gates are the per-service authorization entry points. A pluggable result factory (problem+json vs FHIR OperationOutcome) is the right design; landing it alongside 40+ other changes is not. |
| Burn down 66 `any` in `HttpApiClient.ts` + remove the file-wide eslint-disable | ~250 edits with **zero correctness impact** — every output is already zod-validated at runtime and that validation is tested. Pure type-hygiene, and the least valuable thing to risk regressions on at the end of a large phase. |

Everything else in 18.E2 landed: architecture tests (7 rules), the provider RLS gap they found, `libs/data` interceptor tests, masterdata authz suite, 7 anonymous-object 404s → RFC-7807, `services/hello` deleted.

### Gate F — what landed, and what is deferred

Gate F is explicitly *"Enhancements (highest value first)"* and the prompt's Done-when says **"as prioritized with the sponsor"**. Items were chosen by how directly they close a finding this audit actually made.

**Landed (18.F1 + parts of F2/F3):**

| Item | Why it was picked first |
|---|---|
| `libs/money` value type + 12 property tests | Makes X3 structurally impossible rather than clamped at six call sites |
| Property-based ConsumeExecutor tests (real Postgres, 55 generated interleavings) | X7's whole class — lost updates live in the interleavings nobody thought of |
| **Tenant-isolation fuzzer** (`tools/ci/check-tenant-isolation.py`) | The prompt's own *"control that would have caught X6/S2 automatically"*. **Found a real disclosure the R2 audit did not name** — 105 blank-tenant rows in `emr.appointment_history` readable by any caller without a tenant claim |
| Command palette (⌘K), permission-scoped | Owed: 18.D2 deleted the dead app-bar search on the promise of this. Also fixed the duplicate `g h` / `g q` binding |

**Deferred — needs a sponsor decision, live infrastructure, or is a feature in its own right:**

| Item | Why |
|---|---|
| F2: server-side sort/filter/paginate on 4 worklists | Real value (SLA-remaining sort especially), but it is an API change across four services plus `DataTable` wiring — a feature, not a remediation |
| F2: keyboard-first worklist mode (j/k/a/r/p, `?` overlay) | Depends on the sort/filter work above to be worth having |
| F2: inline validation on blur, draft autosave | Autosave for SOAP notes needs a storage decision (local vs server draft) with PHI implications |
| F2: offline queue, density/branch persistence, UX telemetry | Telemetry is the highest-value of these — the retry-after-failure metric would have surfaced every U1 silent failure within a week — but it needs a privacy review of the event shape before it ships |
| F3: OpenBao dynamic Postgres credentials | Requires a running OpenBao; replaces the static `hbmp_app` that 18.B2 just established |
| F3: audit-topic anomaly detection | Needs the streaming stack live |
| F3: DAST/Schemathesis, SBOM + cosign + SLSA, Pact | All need CI infrastructure this environment cannot exercise; wiring them unverified would repeat the Q1 mistake (a gate committed and never run) |
