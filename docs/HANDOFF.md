# Mersal HBMP — continuation guide

> Rewritten 2026-07-30 (phase 24 Gate 7). The previous version was dated 2026-07-25 and was still largely a
> **phase-0** document: it described Keycloak as the identity provider (replaced by the in-app
> identity-service in phase 17, ADR-0015), told you to start `hello-service` (long gone), and reported
> **"107 tests green"** against a suite that now runs ~2,160. Every number below is measured, and where
> something is unproven it says so.

## What this is

A service-oriented Healthcare Benefit Management Platform for Mersal Foundation's refugee beneficiaries.
22 .NET 8 services, a React/TypeScript SPA, PostgreSQL with row-level security, all self-hostable.
Design set: `HBMP-Design/` (docs 0A–46; 41 was never written). Build prompts: `HBMP-Design/claude-code-prompts/` — one
sub-prompt ≈ one PR. Conventions that apply to every change: root `CLAUDE.md` (auto-loaded). Where the
build has got to: `docs/BUILD-STATUS.md`.

**Golden rule, unchanged:** read the design docs a prompt names *before* coding; if reality diverges from
a doc, flag it in the commit rather than silently deviating.

## Getting a working environment

```bash
# .NET 8 SDK is user-local at ~/.dotnet — use the wrapper, it sets DOTNET_ROOT and PATH.
./dotnet.sh build HbmpPlatform.sln -c Release

# THE FULL SUITE. --with-db is not optional.
./dotnet.sh test HbmpPlatform.sln -c Release --with-db
```

**`--with-db` is the difference between a green run and a meaningful one.** ~100 integration, concurrency
and RLS tests are gated on `Skip.If(<SERVICE>_TEST_DB is null)`. Without the flag every one of them
answers "skip" and the suite still reports success — the consume/dispense concurrency proofs, the RLS
isolation suites and the break-glass lifecycle among them. The flag points them at the Compose Postgres
using the same variable list CI exports, and fails loudly if the database is unreachable rather than
letting the run skip everything quietly.

Frontend needs **Node 22** (Node 20 fails on `node:sqlite`):

```bash
cd apps/web && npx vitest run          # 377 tests
```

Infrastructure: `infra/compose` (Tier 1, single node). Postgres is published on **55432**, not 5432.

## Measured state, 2026-08-20 — `master`, after the stack landed

| | |
|---|---|
| Backend suite | **3,850 passed, 0 failed, 0 skipped** (38 assemblies, `--with-db`) — measured 2026-08-20 |
| Web suite | **1,487 passed, 0 failed** (116 files, incl. axe over every route x locale x theme) — measured 2026-08-20 |
| OpenAPI drift | **22 specs match the running services** — measured 2026-08-20 |
| Migration replay | **247 files, two consecutive passes, exit 0 both** — measured 2026-08-20 |
| Tenant-isolation fuzzer | **153 tenant-scoped tables proven**, 2 declared RLS-free — measured 2026-08-20 |
| Domain coverage | **82.5%** against an enforced floor of 58 — *last measured 2026-07-30, not re-run since* |
| Overall coverage | **50.7%** against an enforced floor of 45 — *last measured 2026-07-30, not re-run since* |
| Gate scripts in `tools/ci/` | 20 |

**Seven stacked branches landed on `master` on 2026-08-20** (`2a19354`, `ed659a3`) after the whole gauntlet
above ran on the merged tree. Everything below the finance pass is now in `master` and nothing is stacked.
The suite rows and the four gate rows were measured for this update. **The two coverage rows were not**, and
say so rather than being carried forward under a new date — the habit that made an earlier version of this
file describe Keycloak and 107 tests. `tools/ci/coverage-floors.json` remains the source of truth for any
figure that disagrees.

Coverage comes from `tools/ci/coverage-report.py`, which merges cobertura reports as a UNION. The previous
gate summed them, and `dotnet test` writes one per test assembly, so 161 files were counted more than once
and the domain denominator came out at 22,625 lines for a layer with 12,488 physical ones. **If a coverage
figure anywhere disagrees with `tools/ci/coverage-floors.json`, that file is the source of truth and the
other number is stale.**

## What is deliberately not true yet

Written down because a handover that lists only achievements is how the next person rediscovers these the
expensive way.

- **GitHub Actions has not run since 2026-08-11: the account is billing-blocked.** Every job on every open
  PR reported `FAILURE`, and not one of them started. The annotation is the same on all fifteen: *"The job
  was not started because recent account payments have failed or your spending limit needs to be increased."*
  This is worse than a red build and reads identically to one. A red check normally means a gate ran and
  found something; here the scoreboard is red because there is no game. **Until billing is fixed, the local
  gauntlet is the only verification that exists** — `./dotnet.sh test HbmpPlatform.sln -c Release --with-db`,
  the `apps/web` suite, `tools/ci/check-openapi-drift.sh`, `tools/ci/apply-migrations.sh` and the scripts in
  `tools/ci/`. Two of those cannot run locally at all (see `gate-freshness` below), so "green locally" is a
  strictly smaller claim than "green in CI" and should be written as the smaller one.

- **`tenant_id = ''` is down to 341 rows in ONE table, and the survivor may not be debt at all.** Measured
  against the Compose database on 2026-08-20 by asking `information_schema` for every column named
  `tenant_id` and counting: **`identity.role_scope` = 341, every other table 0.** The entry used to read
  1,191 rows across 7 tables. `role_scope` is the platform-wide role-to-scope catalogue — reference data
  that is the same for every tenant — so an empty tenant there is plausibly *correct* and the six real
  offenders are gone. **That is a question, not a conclusion:** nobody has decided in writing whether
  `role_scope` is tenant-scoped, and until someone does, the `CHECK (tenant_id <> '')` cannot be written
  because it would either be wrong or need an exemption nobody has justified. The upstream cause is
  unchanged and still worth knowing — 64 entities declare `public string TenantId { get; set; } = "";`, so
  a write path that forgets to set it stores an unscoped row.
- **Outbox atomicity: 55 call sites became 6, in 4 files.** The ratchet in
  `docs/quality/outbox-atomicity-debt.txt` worked exactly as intended — the number can only go down, and it
  went down by 49. What is left, verified against the register on 2026-08-20:
  `services/admin/Api/BranchAssignmentService.cs` (1), `services/case/Api/Beneficiary360Endpoint.cs` (1),
  `services/emr/Api/Reminders.cs` (1), `services/pharmacy/Api/Dispensing.cs` (3). Each still commits its
  event separately from the state change it announces, so a crash between the two loses the event with
  nothing recording it was owed.
- ~~**Two invariants are unproven**~~ — **CLOSED, and this entry was stale.** `INV-DEDUPE-SURVIVES-RESTART`
  is proven against the DURABLE store (`ProcessedEventDurabilityTests`), `INV-OUTBOX-SURVIVES-CRASH` by the
  library + architecture pair, and CI has NOT passed `--allow-unproven` since 24.3 — the exit criterion this
  entry describes as pending was met before it was written. The registry reports **zero** unproven entries
  today. Left visible rather than deleted, because a handover that quietly drops its own open items teaches
  the next reader to distrust the list; struck through, it says the check was done.
- ~~**`services/inventory` has no per-module coverage floor.**~~ **CLOSED — and it was never only inventory.**
  Measuring found SIX modules with no floor at all: `libs/time:Lib` (72), `services/audit:Domain` (99),
  `services/provider:Api` (1) and inventory's three. `raise-floors.py` iterated only over floors that ALREADY
  EXISTED, so a new module could never acquire one — the ratchet had silently stopped covering new code,
  which is the code most likely to need it, and no gate fails for a module that is simply ABSENT. The tool
  now adopts unfloored modules at measured-minus-1 (`--new-only` separates that from tightening existing
  floors), with a selftest for the case. **`services/inventory:Api` measured 0.0% (0/459)** — see below.
- ~~**`services/inventory:Api` has NO tests at all** (0 of 459 lines).~~ **CLOSED.** It has an endpoint test
  host now — `services/inventory/Tests/InventoryApiFactory.cs` and `InventoryEndpointTests.cs`, the
  `EmrApiFactory` pattern this entry prescribed — and its floor in `coverage-floors.json` reads **83.0**,
  not the 0 that enforced nothing. Left struck through rather than deleted, per this list's own rule.
  The fix is an endpoint test host (emr's `EmrApiFactory` is the pattern), not a number.
- **`services/notification:Api`: the 85% floor is now 66.0, and I have NOT re-measured the actual.** The
  entry below described a red gate at 60.1% against 85. The floor moved, and `check-floor-monotonicity.py`
  passes, so the decrease was documented as that gate requires. What nobody has re-run is the coverage
  measurement itself — it needs a full instrumented suite, which was not part of the 2026-08-20 landing.
  **So the honest status is "unknown", not "green".** The original finding, kept because it explains what
  the number is about:
  Caused by commit `fe164b6`, which added `DomainEventConsumer.cs` (198 lines) and `EscalationSweeper.cs`
  (102 lines) — 300 of that layer's 550 lines — with tests covering only the parse seam. Verified independent
  of the floor work above: the same failure occurs with `coverage-floors.json` at its previous contents. Two
  honest routes, and they are a decision rather than a chore: write tests for the sweeper's DB pass and the
  consumer's flag logic (the gate's own advice, "Write the test instead"), or acknowledge that the layer's
  COMPOSITION changed — 300 lines of background-service plumbing is not the endpoint layer the 85 was set
  against — which needs an ADR naming the module, per `check-floor-monotonicity.py`.
- **Four web suites were a calendar time-bomb, now defused.** `reception-booking`, `callcentre`,
  `callcentrebooking` and `reception-dashboard` pinned fixtures to July 2026 while the booking calendar
  defaults its month to `new Date()`. They went red at midnight on 1 August 2026 — 14 tests, four files, no
  code change behind any of them — and would have failed on whoever's commit happened to land next.
  `freezeClock()` in `apps/web/test/helpers.tsx` now pins the date at file scope. **If you write a fixture
  with an absolute date, freeze the clock in the same file.**
- **Phase 25's five sponsor decisions (D1–D5) were RATIFIED as recommended on 2026-08-01** — closed, but keep
  the lesson. Record: `docs/decisions/phase-25-sponsor-pack.md`. Nothing was overturned, so no DPIA and no
  DPO/Medical Director signature was needed. **One field is deliberately still blank**: the signatory's
  printed name and role, which nobody but they can supply.

  **The lesson, which generalises past phase 25.** Writing the decisions out for a non-engineer forced the
  question *"what is each of these actually enforced BY?"*, and D5 answered **nothing** — no reference to
  vaccines, injectables or any medicine identifier existed anywhere in inventory-service, so "vaccines are
  pharmacy stock" was a rule people had to remember while D1–D4 were held by a `CHECK` constraint, a unique
  index, a five-fact test suite and a role-reach mode. The ADR had described all five in the same confident
  tone. **When you write a decision table, put the mechanism beside the answer** — "we decided X" and "the
  platform does X" read identically in prose and are not the same claim. Closed by ADR-0029 §4.1: item
  creation asks masterdata whether the thing is a medicine and refuses it if so, *and refuses it when
  masterdata is unreachable*, so the gate cannot open quietly during an outage.
- **The a11y-contrast job had never once executed its assertions.** It failed with
  `Timed out waiting 120000ms from config.webServer` — which reads like a slow server and was actually a
  command that never ran. Playwright's `webServer.command` was
  `pnpm --filter @mersal/web preview --port 4173 --strictPort`, and **pnpm parses `--port` as one of its own
  options**, so the process died instantly and Playwright spent two minutes waiting for a server nobody had
  started. Port and host now live in `apps/web/vite.config.ts`'s `preview` block and the command is a bare
  `npx vite preview` — flags that must survive two argument parsers eventually do not.

  **On its first real run it failed 33 of 48 — the gate working, not a regression.** Four defects, all
  genuine, none of which any other check could see:
  - `--text-3` light was `#6b7c82`: **3.94:1 on `--surface-0`, 4.35 on white, 4.17 on `--surface-2`** — below
    AA on every light surface, and it is the colour every worklist `<th>` renders in. Now `#5d6b71` (≥4.99).
  - `--text-3` dark was `#7fa0a0`: 4.18:1 on `--surface-2`, so it *passed on the page background and failed
    inside every card*, which is where micro-labels live. Now `#8bb0b0` (≥5.02).
  - `.app-avatar` was **1.78:1** — `color: var(--on-accent, #fff)` immediately followed by `color: #fff`,
    which overrode it. Correct in light theme by luck (`--accent` is dark teal there), catastrophic in dark
    (it is light teal). **A fix defeated by the line below it**: token, comment and first declaration all said
    the right thing while the paint did not.
  - `/login` had **no `<main>` landmark at all** — the login page renders outside the signed-in shell. It
    surfaced as a 15s `waitForFunction` timeout, which reads like a slow route. Fixed on the page, not by
    relaxing the wait: that wait is what stops an empty page being audited and reported clean.

  **`apps/web/test/token-contrast.test.ts` now guards the flat-token case** in the fast jsdom suite (37
  assertions, 11ms) — every text × surface pair, `--on-accent` vs `--accent`, and every status fg/bg, in both
  themes. Its arithmetic was cross-validated against axe: it reports 4.17:1 for the shipped `--text-3`, the
  same number the browser measured. **It does not replace the browser job**, which measures *composited*
  colour — opacity, gradients, overlays — that no hex-pair arithmetic can see.

  Why it went unnoticed for months is worth keeping: `--text-1` and `--text-2` carry their measured ratios in
  a comment beside them and `--text-3` carried none. The two that had been checked said so; the one that never
  had was silent, and silence read as fine.

  **Running it locally needs pnpm**, which is broken on this machine
  (`ERR_UNKNOWN_BUILTIN_MODULE` from the pnpm/Node pairing), so `@playwright/test` is in the lockfile but not
  installed. The vite half is verifiable without it: `cd apps/web && npx vite preview` must answer
  `http://127.0.0.1:4173`. The host is pinned because vite's default binds `localhost`, which resolves to
  `::1` first on some hosts — the server would be up and the probe would still time out.
- **A gate with no local entry point is a gate you learn about from CI.** Three committed OpenAPI specs
  (`approvals`, `patient`, `policy`) were stale on 2026-08-01 and `openapi-drift` — a blocking gate — had been
  **red in CI for a day** while local runs reported every other gate green. Both halves of that sentence
  matter. The specs are regenerated and the gate passes now; the reason it went unnoticed is the part worth
  keeping.

  The drift comparison lived **only** inside `.github/workflows/backend-ci.yml`. Every other gate has a
  script under `tools/ci/`, so "run the gates locally" covered ten of eleven and nobody noticed the
  eleventh was missing — the only way to run it was to push and wait ~8 minutes for the scoreboard, which
  is not something anyone does before a commit. **Before saying "all gates green", check that the set you
  ran is the set CI runs**, or the sentence is about your tooling rather than the code.

  Now: `DOTNET=./dotnet.sh tools/ci/check-openapi-drift.sh` (add `--fix` to regenerate in place). CI calls
  the same script rather than its own copy, and `openapi-generate`/`openapi-drift` are in
  `check-gate-freshness.py`'s `REQUIRED_GATES` so the watchdog alarms if they stop running rather than
  merely failing. **Any new gate belongs in `tools/ci/`, not inline in the workflow.**
- **Migration 0010's own header overstates what it does.** FORCE ROW LEVEL SECURITY stops a *non-superuser*
  owner bypassing a policy. The owner here, `hbmp`, is SUPERUSER + BYPASSRLS, so the protection it
  advertises does not apply in this deployment. What actually holds is that requests are served by
  `hbmp_app` (NOBYPASSRLS).
- **Branch protection does not exist** on this repository — GitHub returns 403 on this plan for a private
  repo. Nothing enforces review or blocks a force-push.
- **The Playwright contrast job is not blocking yet.** jsdom cannot paint, so `color-contrast` is disabled
  in the vitest a11y sweep by design; the browser job that *can* check it does not yet fail the build.

## The gates, and why they are shaped that way

`.github/workflows/backend-ci.yml` runs every gate even when an earlier one fails, and fails the job at
the end. That is not a style preference. Around 27 June the pipeline began dying at the migration-compat
gate on every push, and everything after it — route coverage, scope drift, migrations, the tenant-isolation
fuzzer, the entire test suite, the coverage floor — never executed for a month. A skipped gate and a
passing gate are equally silent, which is what made it invisible.

The same failure has a second form worth knowing about: a gate that runs and checks nothing. The a11y
sweep audited an empty element on 55 of 112 routes. The OpenAPI drift gate passed when generation produced
zero specs. The `paths:` filter excluded `docs/**`, so the one commit whose entire content was an OpenAPI
contract fix triggered no run at all. **When a gate looks green, the useful question is not "did it pass"
but "what did it actually read".**

## Where to start reading

1. `docs/BUILD-STATUS.md` — phase by phase, with a legend that now exists and is enforced
2. `docs/quality/invariant-registry.yaml` — every invariant the platform claims, and the tests that prove it
3. `docs/AUDIT-R2-E2E.md`, `docs/AUDIT-R3-COMPLETION-MAP.md` — the standing audit findings
4. `docs/quality/deferred-findings.md` — real divergences found in passing and deliberately left, each
   with the reason and what closing it means. Short by design: an entry leaves by being fixed or by
   being retired in writing, never by ageing
5. `docs/adr/` — decisions, including ADR-0027 on coverage and gate integrity
6. `docs/runbooks/` — operational procedures, including the 2026-07-30 history purge
