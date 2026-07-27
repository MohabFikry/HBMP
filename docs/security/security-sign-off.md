# Security sign-off — Phase 18 (audit R2 remediation)

- **Date:** 2026-07-27
- **Scope:** every finding in [`docs/AUDIT-R2-E2E.md`](../AUDIT-R2-E2E.md) (X1–X10, S1–S9, W1–W8, U1–U10, Q1–Q4)
- **Verified against:** `HbmpPlatform.sln` — 28 projects, **1133 backend tests** (1 skipped), 0 warnings;
  **106 frontend tests**, `tsc --noEmit` clean, 0 ESLint errors; live PostgreSQL on :55432

This document is written to be **checkable, not reassuring**. Where something is closed it says how it is
proven; where something is not, it says so plainly and names what remains. A sign-off that overstates is
worse than none, because it stops the next person looking.

---

## 1. What is closed, and what proves it

| Area | Closed | Proof that survives a refactor |
|---|---|---|
| **Benefit correctness** (X1, X2, X3, X8) | Coverage limits accumulate; rollups preserve adjustments; allowed ≤ contract tariff on every decision kind; pricing is quantity-aware | `Money.CapTo` + 12 property tests (~120k cases), `BatchRollupService` as the single rollup authority, claims suite 173 tests |
| **Concurrency & time** (X7, X9, X10) | No lost update on concurrent consume; eligibility cache is service-aware; limit reset respects its period | Property-based `ConsumeExecutor` suite — 55 generated interleavings against real Postgres asserting Σ fulfillment == accumulator; `NoBareClockArchitectureTests` bans bare `UtcNow` |
| **Secrets** (X4, X5) | No committed credentials; rotation applies on restart | 3 gitleaks rules (config values, `??` fallbacks, URI-embedded); `ClientSeedingTests` scans both seeders for surviving literals |
| **Tenant isolation** (X6, S1, S2) | Every runtime connection is a non-superuser role with a bound GUC; no fail-open policy anywhere | **`tools/ci/check-tenant-isolation.py` — 92/92 tenant-scoped tables proven**, enumerated from `information_schema` rather than a maintained list; self-tests against a deliberately fail-open policy |
| **Authorization** (S3–S6) | Admin + identity-admin gated at the route table with MFA; scopes never fail open; patient PHI reads are scoped, projected and audited | Route-metadata tests (a future ungated route fails the build), `ScopeGrantTests`, `ScopeIntegrityTests` (policy bundles vs the identity seed) |
| **Issuer hardening** (S4, S7, S9) | CSRF closed on all three credential forms; transport security first in the pipeline; per-IP rate limits on `/connect/*` | `IssuerEndpointSecurityTests` asserts the route table and the middleware order |
| **Reachability** (W1–W8) | Sessions renew; branch switching works end to end; interop, report-access approval, identity admin and the SPA are reachable; doctor↔branch validated | `ReachabilityTests` read `compose.yaml` + `kong.yml` directly; a service without a route fails the build |
| **UX safety** (U1–U10) | No silent write failures; typed bilingual errors; truthful status chips; navigable at 375px; Africa/Cairo dates; axe clean on every route × EN/AR × light/dark | `useWrite` (per-form idempotency key), `writeErrorMessage`, route-wide axe sweep driven off the portal catalog |
| **CI truth** (Q1, Q2, Q4) | Route coverage, identity tests, OpenAPI drift and the coverage ratchet all enforced; one authoritative set of gate scripts | `CiGateTests` assert the **wiring** — a script nobody calls is indistinguishable from one that passes |

### Two things the remediation found that the audit did not

1. **`emr.appointment_history` — 105 rows readable without a tenant.** The rows carried
   `tenant_id = ''`, and `UseHbmpRls` bound the empty string for any principal without a tenant claim, so
   `'' = ''` matched. An append-only clinical history table was readable by an unauthenticated caller. Closed
   at the class level (`libs/data` now binds a sentinel no row can carry) **and** in the data (emr `0008`,
   with a non-blank CHECK that immediately caught two test fixtures reproducing the same defect).

2. **141 rule/role pairs no token could satisfy.** The policy bundles named roles that the identity seed
   never granted the matching scope to. The entire claims decision surface, break-glass for every clinician,
   and `medical_director`'s whole oversight surface were unreachable — silently, because a missing grant
   produces a 403, not an error. 135 closed by identity `0004`/`0005`.

---

## 2. Operational gates — NOT verified in this environment

These are implemented and committed but could not be exercised here. **They must be confirmed on a live
stack before this sign-off means anything in production.**

| Gate | What is unverified | How to verify |
|---|---|---|
| Kong edge JWT (S8) | The plugin config parses and the routes are correct, but no request has been proxied through it | Bring up `infra/compose`, `curl` an `/api/v1` route without a token → expect 401 at the gateway |
| FHIR façade reachability (W3) | Compose block + Kong route committed; never called | `curl http://localhost:8000/fhir/r4/metadata` → expect the CapabilityStatement (this path is exempt from the JWT plugin by design) |
| SPA in compose (W6) | Service block added; the image has not been built | `docker compose up web` and load `http://localhost:5173` |
| Playwright contrast job (U6) | Spec + CI job committed; **no Chromium binary on this machine** | `pnpm --filter @mersal/web test:a11y-contrast` in CI |
| Identity issuer keys | Kong validates against a registered RS256 public key that must match what OpenBao holds | Export the key, set `IDENTITY_JWKS_PUBLIC_KEY`, confirm a real token validates at the edge |

A note on the toolchain here: `pnpm` requires Node ≥22 and this machine runs v20, so frontend suites were run
through the workspace's own `node_modules/.bin/vitest` and `tsc`. That is an environment limitation, not a
repository change — CI is unaffected.

---

## 3. Open — requires a decision, not more work

**Six roles are named by policy rules and do not exist in the frozen role vocabulary:**
`claims_reviewer`, `manager`, `network_manager`, `approvals_team`, `finance_approver`,
`call_center_supervisor`.

Each is a real role in `10-role-matrix.md`. Adding one changes the token contract **and** the SPA's
role→portal mapping, so it is a product decision rather than a seed fix. They are declared in
`ScopeIntegrityTests` with reasons and a staleness check.

**The consequence while they stay open, stated plainly:** for `claims:settle` and `claims:export` the rule
names `finance` and `manager`. Only `finance` exists. So the second half of what was designed as a
dual-control pair is currently one role — the SoD split those rules describe is not fully realisable until
this is decided. The handler-level SoD checks (creator ≠ releaser) still apply.

---

## 4. Deferred enhancements (Gate F)

Gate F is scoped in the prompt as *"as prioritized with the sponsor"*. What landed was chosen by how
directly it closes a finding this audit made; what did not is recorded with reasons in
[`docs/PHASE-18-TODO.md`](../PHASE-18-TODO.md).

The highest-value deferred item is **UX telemetry** — a privacy-safe `{event, screenKey, roleKey,
durationMs, outcomeClass}` on the existing audit transport. The retry-after-failure metric alone would have
surfaced every U1 silent-failure defect within a week of pilot. It needs a privacy review of the event shape
before it ships, which is the right order.

Three structural refactors from Gate E are also deferred rather than half-done (`libs/testing` extraction,
`HbmpGate<TPolicy>` consolidation, the `HttpApiClient` `any` burn-down). The first two touch the tests and
gates that prove tenant isolation; doing them alongside forty other changes trades a known-good state for a
cosmetic gain, with silent failure on the downside.

---

## 5. Sign-off position

The R2 findings are closed and each is held by a test or a gate that fails the build on regression — not by
a fix that happens to be correct today. Two additional defects found during the work, one of them a live
disclosure, are closed the same way.

**This is not a statement that the platform is ready for patient data.** It is a statement that the audit's
findings are addressed and provable. Before pilot: the operational gates in §2 must pass on a live stack,
the role decision in §3 must be made, and a DPIA sign-off must exist per `20-compliance-checklist §6`.
