# Phase 24 — Coverage, CI truth & repo hygiene (do it properly, not quickly)

**Goal:** Make the quality gates tell the truth, make them *impossible to go blind again*, raise real coverage on the code that carries risk, and close the two outstanding repo items — with no shortcuts, no lowered bars, and no rubber-stamped floors.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Audits: [`../../docs/AUDIT-R2-E2E.md`](../../docs/AUDIT-R2-E2E.md), [`../../docs/AUDIT-R3-COMPLETION-MAP.md`](../../docs/AUDIT-R3-COMPLETION-MAP.md)

> **Sponsor decision, recorded here so you do not re-litigate it:** the platform is pre-production, nobody is using it, and there is **no time pressure**. Therefore **the coverage floors are NOT lowered.** Option 3 from the CI report is chosen: keep the bar, write the tests. Where the report offered "lower the floors with an ADR", that option is **rejected** — it was the right call under time pressure and this project is not under time pressure.
>
> **But raising the number is not the goal.** A coverage percentage can be satisfied by testing the easy parts while the money and PHI paths stay untested. The goal is that **every invariant this platform claims has a named test that CI refuses to run without**. Coverage is the proxy; the invariant registry (Gate 2) is the actual deliverable.

## Skills to activate
> `mersal-platform-architect`, `refugee-healthcare-management` (always-on), plus `healthcare-business-rules-engine` (Gate 3 benefit/money tests), `healthcare-database-architect` (Gate 5 migration), `healthcare-uiux-designer` (Gate 4 a11y measurement).

## Context — read first
- The CI run under discussion: 2,140 passed / 2 skipped, DB wired for the first time; `overall 47,920/186,464 = 25.7%` (floor 45), `domain 10,620/22,625 = 46.9%` (floor 58).
- [`../../docs/AUDIT-R2-E2E.md`](../../docs/AUDIT-R2-E2E.md) §2 (X1–X6 correctness findings) and [`../../docs/AUDIT-R3-COMPLETION-MAP.md`](../../docs/AUDIT-R3-COMPLETION-MAP.md) — **these name exactly which untested code matters.** Test writing is ordered by them, not alphabetically.
- `tools/ci/coverage-gate.sh`, `.github/workflows/backend-ci.yml`, `.gitlab-ci.yml`, `libs/architecture/**`, `libs/spec-conformance/**`.
- `CLAUDE.md` — coverage target **≥80% on domain logic**. That is the destination; 58% was only the regression guard on the way there.

## THE ROOT CAUSE (fix this, or everything else recurs)
CI died at an earlier gate around **27 July**. For roughly a month the coverage guard never executed. In that window phases 19–21 landed — claims, profile, callcentre, admin — and coverage fell from >45% to 25.7% **with nobody able to see it**. The gate did not fail; it was never reached.

So the primary deliverable of this phase is not tests. It is: **a gate that cannot be silently skipped, and a number that is published on every run whether or not other gates pass.** Gates 0 and 1 come before any test is written.

---

## Gate 0 — Measure honestly before touching a single floor

```text
Do NOT change any floor value in this gate. Only change what is MEASURED, and prove the change is honest.

0.1 AUDIT THE DENOMINATOR. overall is 186,464 lines while domain is 22,625. Before accepting 25.7% as
    reality, produce a per-assembly / per-directory coverage report and answer, in writing:
    what are the other ~164,000 lines? Expect: generated OpenAPI clients (docs/api/**), EF migration
    scaffolding, generated contract DTOs, Program.cs bootstrap, design-system gallery code.
    Note the shape of the drop: domain fell 58 -> 46.9 (-11pt) but overall fell 45 -> 25.7 (-19pt).
    Overall fell much harder, which is evidence that a large volume of NON-domain code landed. Confirm
    or refute that with the report — do not assume it.

0.2 EXCLUDE ONLY GENERATED CODE, AND MAKE THE EXCLUSION AUDITABLE.
    - Exclusions go in ONE versioned file (tools/ci/coverage-exclusions.txt) with a ONE-LINE REASON per
      entry. No wildcards broader than a generated-output directory.
    - Allowed: files carrying an auto-generated header, scaffolded API clients, EF migration .Designer
      files, generated contract types.
    - FORBIDDEN: excluding hand-written code because it is inconvenient to test. Excluding a service.
      Excluding anything under Domain/. If you find yourself wanting to exclude domain code, that is the
      finding, not the fix.
    - Add a CI check that fails if the exclusion file grows without an accompanying ADR reference in the
      commit message. Gaming the denominator must be as hard as lowering the floor.

0.3 RE-MEASURE AND PUBLISH. Emit coverage as a machine-readable artifact per module
    (tools/ci/coverage-report.json: {module, covered, total, pct}) on EVERY run, and print a table in
    the job summary. This artifact is what Gate 1 ratchets against.

0.4 WRITE DOWN THE HONEST NUMBER. Update the ADR (new: docs/adr/0024-coverage-and-gate-integrity.md)
    with: the pre-exclusion numbers, the post-exclusion numbers, the exclusion list and why each entry
    qualifies, and the statement that floors were NOT lowered.

ACCEPTANCE: a per-module coverage table exists; every exclusion has a written reason; the domain
denominator is unchanged or larger (never smaller by excluding hand-written domain code); the ADR states
both the old and new measured values.
```

## Gate 1 — A gate that cannot go blind (this is the real fix)

```text
The coverage guard was correct for a month and useless for a month, because the pipeline never reached
it. Fix that class of failure, not just this instance.

1.1 GATES RUN INDEPENDENTLY. Restructure backend-ci.yml (and .gitlab-ci.yml) so a failing early gate
    does NOT prevent later gates from running and REPORTING. Use fail-at-end semantics: every gate
    executes, each records pass/fail, the job fails at the end if any failed. A red build must still
    tell you the coverage number, the route-coverage result and the scope-drift result.
    This single change would have surfaced the coverage collapse in late July.

1.2 STALENESS ALARM. Add tools/ci/check-gate-freshness.py: reads the last successful execution
    timestamp of each named gate (from the published artifacts / workflow history) and FAILS if any gate
    has not actually EXECUTED in N days (default 7). A gate that has not run in a week is treated as a
    failing gate, because that is what it is. Wire it into the daily scheduled workflow.

1.3 MONOTONIC RATCHET. Move floors out of workflow YAML into tools/ci/coverage-floors.json
    ({module: pct}). Add tools/ci/check-floor-monotonicity.py that compares the committed floors against
    git HEAD~ and FAILS on any decrease unless the commit message contains an ADR reference
    (`ADR-\d{4}`) AND that ADR file exists and mentions the module. Lowering a bar becomes a documented
    act, never a quiet one.

1.4 AUTO-RAISE. After a green run, if a module's measured coverage exceeds its floor by >3 points,
    CI opens (or updates) a PR raising that floor to measured-minus-1. The ratchet tightens by itself;
    nobody has to remember. This is what makes the 80% target reachable instead of aspirational.

1.5 SELF-TEST THE GUARDS. Every script above gets a --selftest mode with fixtures proving it FAILS when
    it should (the tenant-isolation fuzzer already does this — follow that pattern). A guard with no
    failing-case test is a guard nobody has proven works.

ACCEPTANCE: a build failing at gate 2 still reports gates 3-20; a stale gate fails the daily run; a
floor decrease without an ADR fails; a coverage jump opens a floor-raise PR; every guard has a selftest.
```

## Gate 2 — The invariant registry (the future-proof deliverable)

```text
Coverage percentage is a proxy. What actually must never regress is the set of guarantees the design
docs claim. Make those first-class.

2.1 CREATE docs/quality/invariant-registry.yaml. One entry per platform invariant, each with:
    id, statement (one sentence, in the language of the design doc), source (doc + section),
    severity (Critical|High), and tests[] — the FULLY QUALIFIED test names that prove it.

    Seed it from the invariants already written down, at minimum:
    - CLAUDE.md invariants 1-5 (atomic idempotent consume/dispense; min-necessary field-level access;
      immutable hash-chained audit + no hard deletes; WCAG 2.2 AA + Arabic RTL; provider/tenant isolation).
    - AUDIT-R2 X1-X6: coverage_limit.consumed_value is actually incremented; batch rollups preserve
      applied adjustments; allowed amount is capped at contract tariff (Math.Min, and Adjust is capped
      too); contract price accounts for line quantity; concurrent consume of DIFFERENT lines does not
      lose an update; no committed credentials.
    - Doc 37 §6: sensitive results stay existence-only without a grant, INCLUDING for the approval team.
    - Doc 39 §7: profile composed under the caller's token, never a service account; payload never
      contains a withheld field.
    - Doc 40 §7 (A1): the platform-admin flag never bypasses projection/ABAC/RLS/branch scope.
    - RLS: no-GUC yields ZERO rows, and the negation (an empty predicate WOULD have returned rows).

2.2 ADD tools/ci/check-invariant-registry.py: fails if any registry entry names a test that does not
    exist, is skipped, or is excluded from the run. Deleting or [Skip]-ing an invariant test now breaks
    the build with the invariant's own sentence in the error message. Wire into both pipelines.

2.3 ADD THE MISSING ONES. Any registry entry whose tests[] is empty is a work item for Gate 3. Do not
    delete an entry to make the check pass — an invariant with no test is precisely what this file is
    for surfacing.

ACCEPTANCE: registry covers every invariant in CLAUDE.md + docs 37/39/40 + AUDIT-R2 §2; the checker
fails on a renamed, deleted or skipped test; zero entries have empty tests[] by the end of Gate 3.
```

## Gate 3 — Write the tests, in risk order (not alphabetical)

```text
Target: domain >= 58% (restore the guard), then continue toward the CLAUDE.md target of 80%. But order
the work by RISK, using the audits — the point is not the number.

TIER 1 — money and entitlement (AUDIT-R2 X1-X3, X5). Test first regardless of current coverage:
  - policy: consumed_value incrementing from OrderLinesConsumed / RxLinesDispensed; limit reset;
    remaining computation; LIMIT_EXCEEDED actually firing; carry-forward on plan change.
  - claims: adjustment preservation through BatchRollup.Compute at the Decided->SettlementIssued
    freeze; allowed-amount cap incl. ClaimDecisionKind.Adjust; contract price x quantity.
  - eligibility: the full decision matrix incl. suspended/closed/waiting-period.
  Property-based tests where the input space is numeric (FsCheck or similar): a cap that holds for
  three hand-picked values is not a cap.

TIER 2 — PHI boundaries:
  - profile projection matrix over the SERIALIZED payload, every role x every section.
  - sensitive-result gate incl. the approvals-team case.
  - min-necessary FieldProjector per service.
  - RLS isolation per service with the negation assertion.
  - break-glass: elevation is loud, time-boxed, audited, and expires.

TIER 3 — concurrency and idempotency:
  - consume/dispense: same key replays once; DIFFERENT lines in parallel do not lose an update
    (the X6 lost-update case); partial fulfilment leaves the remainder active.
  - outbox: event survives process kill between business commit and enqueue.
  - dedupe survives a restart (requires the durable IProcessedEventStore — see phase 22 Gate 3.4).

TIER 4 — the newly-landed, thinly-tested modules from the blind window: callcentre, admin, case,
  finance, audit-service (3 tests today), document, masterdata.

RULES:
  - A test that asserts a service returns 200 is not coverage of a rule. Assert the RULE.
  - No test may be written to raise a percentage on code with no behaviour (getters, DTO mapping).
    If a module's coverage is low because it is all plumbing, that is a Gate 0 exclusion question,
    not a test-writing task.
  - Every test written for a registry invariant gets registered in invariant-registry.yaml as it lands.
  - Fix any BUG the new tests expose in the same PR, with the failing test committed first (red, then
    green) so the fix is provably tested. Several of these tests are EXPECTED to fail on first run —
    X1-X3 are open findings. A test that passes immediately on a known-broken path is a wrong test.

ACCEPTANCE: domain >= 58% with the floors untouched; every Tier 1 and Tier 2 invariant has a named
registered test; every audit finding X1-X6 has a regression test that fails against the pre-fix code
(prove it by running the test on the reverted commit and recording the output in the PR).
```

## Gate 4 — Close the measurement holes the tests cannot see

```text
From AUDIT-R3: some suites pass while measuring nothing. Percentage-blind, so Gate 3 will not catch them.

4.1 VACUOUS a11y: the 9 policy-family screens and CallCentre have no fixture path (they default to
    createHttpPolicyApi() / raw fetch), so the axe sweep only ever exercises their ERROR state. Add
    DevApiClient fixtures, re-run, and FIX the violations that appear. Expect real findings.
4.2 CONTRAST is disabled under jsdom by design. Extend the Playwright job to every route in EN+AR x
    light+dark and make it blocking.
4.3 ARABIC: HttpApiClient sets `ar` to the English string for every server label (~45 render sites), so
    the AR UI shows English in live mode and no test sees it. Use bilingual pairs where the API returns
    them, a shared enum-label map where it returns enums, and add the test that fails when a rendered
    AR label equals its English source while an Arabic value was available.
4.4 SKIPPED TESTS: 2 skipped in the last run. Identify both. Either make them run or delete them —
    a permanently skipped test is a lie with a green tick. Add a CI cap: >0 skips outside an
    explicitly-allow-listed set fails the build.

ACCEPTANCE: every route's axe run exercises a POPULATED state; contrast enforced in a real browser;
no English-in-Arabic where an Arabic value exists; zero unexplained skips.
```

## Gate 5 — Migration 0010 (RLS on the owner role) — rehearse, then apply

```text
0010 changes RLS for the owner role. Applied carelessly this is the deny-all trap AUDIT-R2 found armed
in claims/callcentre/admin: every query returns zero rows and the app looks empty rather than broken.

5.1 REHEARSE ON A COPY. Restore a dump of the running dev DB to a scratch database. Apply 0010 there.
    Run the FULL DB-gated suite against the scratch DB (./dotnet.sh test --with-db) plus a manual
    smoke of one read path per service. Record the result.
5.2 PROVE BOTH DIRECTIONS. A test that rows are still visible to the correctly-bound app role, AND the
    negation: with no GUC bound, zero rows (and evidence that N>0 rows exist for a bypassing role).
    Without the negation the test passes on an empty database.
5.3 WRITE THE ROLLBACK before applying: the exact SQL to restore the prior grants/policies, tested on
    the scratch DB.
5.4 APPLY to the dev DB in a maintenance window with a dump taken first. Verify with the same smoke set.
5.5 PREVENT RECURRENCE: extend libs/architecture so any migration that touches ENABLE/FORCE ROW LEVEL
    SECURITY, ALTER ROLE, or GRANT fails the build unless a matching RlsIsolationTests suite exists for
    that schema. The three services that shipped RLS DDL with no GUC binder are how this class was born.

ACCEPTANCE: rehearsal recorded; rollback tested; applied; both directions proven; the architecture
guard rejects a new RLS migration without an isolation suite.
```

## Gate 6 — Purge the 62 MB PDF from history, and make it unrepeatable

```text
6.1 CHECK CONTENT FIRST. Before treating this as housekeeping, open it. If the PDF contains ANY
    beneficiary data, this stops being a repo-size task and becomes a data-handling incident: record it,
    follow docs/runbooks, and note it against the DPIA (doc 20). If it is a drug guide or reference PDF,
    proceed as hygiene.
6.2 PURGE with git-filter-repo (NOT filter-branch — deprecated and slow). Steps, in order:
    - Full mirror backup of the repo, kept offline until 6.5 passes.
    - Confirm every collaborator has pushed; history rewrite invalidates every existing clone.
    - Run the purge; verify the blob is gone (`git rev-list --objects --all | grep <sha>` returns
      nothing) and record before/after repo size.
    - Force-push all refs and tags. Re-protect branches afterwards — protection often blocks the push
      and gets disabled temporarily; re-enabling it is the step people forget.
    - Every collaborator re-clones. Do NOT let anyone merge a stale clone afterwards; that reintroduces
      the blob and the whole exercise is undone.
6.3 REPLACE THE FILE PROPERLY: if the PDF is still needed, store it in MinIO (document-service already
    has the bucket, SSE and ClamAV) or as a release asset, and reference it by URL from the repo.
6.4 PREVENT: add a pre-commit hook AND a CI check rejecting any added blob over a threshold (default
    5 MB) unless it is git-lfs tracked; add the extension to .gitattributes/.gitignore as appropriate.
    Without this the next large file lands the same way.
6.5 VERIFY: fresh clone from the remote, confirm size, confirm the build is green from that clone.

ACCEPTANCE: blob absent from all refs; size recorded before/after; guard rejects a 10 MB test file;
fresh clone builds green; backup retained until verification passes.
```

## Gate 7 — Make the status documents stop lying

```text
Truthful gates are worthless beside untruthful status docs — both are read as "the state of the build".
7.1 docs/BUILD-STATUS.md: delete the ten stale all-☐ rows that contradict the ☑ rows above them
    (:120-129, :143-144); define or replace the undocumented ◪ glyph; add the 24.x rows.
7.2 docs/HANDOFF.md is named as the entry point and is ~12 phases stale (still documents Keycloak,
    hello-service, "107 tests green" against ~2,140). Rewrite it from current reality.
7.3 Reconcile the coverage numbers across CLAUDE.md (80%), .gitlab-ci.yml (COVERAGE_MIN "80", unused)
    and the real floors. ONE source of truth: tools/ci/coverage-floors.json. Everything else references
    it or is deleted. State the ramp to 80% and the auto-raise mechanism (1.4) in the ADR.
7.4 Add a CI check that BUILD-STATUS contains no duplicate sub-prompt ids with conflicting glyphs.
    The contradiction was invisible for weeks because nothing read the file mechanically.

ACCEPTANCE: no status row contradicts another; HANDOFF matches reality; one coverage source of truth;
the duplicate-row checker fails on a seeded contradiction.
```

---

## Order of execution
`0 → 1 → 2 → 3 (Tier 1 → 4) → 4 → 5 → 6 → 7`

Gates 0–2 are infrastructure and come first: measuring honestly, making the gate unskippable, and writing down what must never regress. Only then is test-writing meaningful, because only then does a passing suite mean something. Gates 5 and 6 are independent and may run in parallel with Gate 3 by a second worker.

## Guardrails
- **The floors do not move down.** Not in this phase, not in a follow-up, not "temporarily". The only permitted floor change is upward, by the auto-raise in 1.4.
- **Do not exclude hand-written code to raise a percentage.** Every exclusion carries a written reason and is reviewed as if it were a floor change.
- **Red before green.** Every regression test for an audit finding must be shown failing against the pre-fix code, with the output recorded.
- **No skipped tests without an allow-list entry.** A permanently skipped test is worse than a missing one — it looks green.
- **Never widen an existing test to make a new failure pass.** If the honest measurement in Gate 4 breaks a suite, the code is wrong, not the test.
- Full suite green after each gate (`./dotnet.sh test HbmpPlatform.sln -c Release --with-db` + `pnpm -r test`) — **`--with-db` is mandatory**; a plain `dotnet test` skips ~100 integration/concurrency/RLS tests and reports green.
- Phase 22 (functional completion) and this phase overlap at Gate 3 Tier 4 and Gate 4. Run **22 first** where they collide — testing a screen that cannot write is testing the wrong thing.

## Done when
- [ ] Coverage is measured honestly: per-module report published every run, exclusions limited to generated code with written reasons, ADR-0024 records old and new numbers.
- [ ] A failing early gate no longer hides later gates; a gate that has not run in 7 days fails the build; floors live in one versioned file and cannot decrease without an ADR; green runs auto-raise floors toward 80%; every guard has a selftest.
- [ ] `invariant-registry.yaml` covers every invariant in CLAUDE.md, docs 37/39/40 and AUDIT-R2 §2, with no empty `tests[]`, and CI fails if a named test is deleted, renamed or skipped.
- [ ] Domain coverage ≥ 58% **with the original floors intact**; every X1–X6 finding has a regression test proven to fail pre-fix.
- [ ] Policy/call-centre screens have fixtures so axe measures a populated state; contrast enforced in a real browser EN+AR; no English-in-Arabic where Arabic exists; zero unexplained skips.
- [ ] Migration 0010 rehearsed, rolled-back-tested, applied, proven in both directions; new RLS migrations without an isolation suite fail the build.
- [ ] The 62 MB blob is gone from all refs, its content classified first, replaced by object storage if still needed, and a size guard prevents recurrence; a fresh clone builds green.
- [ ] BUILD-STATUS and HANDOFF are true; one coverage source of truth; a checker prevents the contradictions returning.
