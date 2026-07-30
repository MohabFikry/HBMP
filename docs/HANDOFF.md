# Mersal HBMP — continuation guide

> Rewritten 2026-07-30 (phase 24 Gate 7). The previous version was dated 2026-07-25 and was still largely a
> **phase-0** document: it described Keycloak as the identity provider (replaced by the in-app
> identity-service in phase 17, ADR-0015), told you to start `hello-service` (long gone), and reported
> **"107 tests green"** against a suite that now runs ~2,160. Every number below is measured, and where
> something is unproven it says so.

## What this is

A service-oriented Healthcare Benefit Management Platform for Mersal Foundation's refugee beneficiaries.
21 .NET 8 services, a React/TypeScript SPA, PostgreSQL with row-level security, all self-hostable.
Design set: `HBMP-Design/` (docs 0A–40). Build prompts: `HBMP-Design/claude-code-prompts/` — one
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

## Measured state, 2026-07-30

| | |
|---|---|
| Backend suite | **2,157 passed, 0 failed, 0 skipped** (32 assemblies, `--with-db`) |
| Web suite | **377 passed** (35 files) |
| Domain coverage | **82.5%** against an enforced floor of 58 |
| Overall coverage | **50.7%** against an enforced floor of 45 |
| CI gates | 17, all green on the last backend commit |

Coverage comes from `tools/ci/coverage-report.py`, which merges cobertura reports as a UNION. The previous
gate summed them, and `dotnet test` writes one per test assembly, so 161 files were counted more than once
and the domain denominator came out at 22,625 lines for a layer with 12,488 physical ones. **If a coverage
figure anywhere disagrees with `tools/ci/coverage-floors.json`, that file is the source of truth and the
other number is stale.**

## What is deliberately not true yet

Written down because a handover that lists only achievements is how the next person rediscovers these the
expensive way.

- **1,191 rows across 7 tables carry `tenant_id = ''`** and belong to no tenant — invisible to every real
  tenant, visible to any session binding an empty one. Root cause is upstream: 64 entities declare
  `public string TenantId { get; set; } = "";`, so a write path that forgets to set it stores an unscoped
  row. The backfill and a `CHECK (tenant_id <> '')` belong WITH that fix, not before it, or the constraint
  just moves the failure to insert time in production.
- **55 outbox call sites** still commit their event separately from the state change it announces, so a
  crash between the two loses the event with nothing recording it was owed. Ratcheted one-way in
  `docs/quality/outbox-atomicity-debt.txt` — the number can only go down.
- **Two invariants are unproven**: `INV-DEDUPE-SURVIVES-RESTART` (needs the durable `IProcessedEventStore`
  from phase 22) and the Api-layer coverage gap. CI runs the registry checker with `--allow-unproven`
  until they are covered; dropping that flag is the exit criterion.
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
4. `docs/adr/` — decisions, including ADR-0027 on coverage and gate integrity
5. `docs/runbooks/` — operational procedures, including the 2026-07-30 history purge
