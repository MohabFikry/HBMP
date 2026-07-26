# Phase 16 — Audit Remediation (security, wiring, integrity)

**Goal:** Close every finding from the 2026-07-26 build audit ([`../../docs/AUDIT-2026-07-26.md`](../../docs/AUDIT-2026-07-26.md) — **read it first, it carries the evidence file:lines**). The theme: the platform's defenses and wiring were *built* but not *engaged*. This phase engages them: durable outbox, admin authz middleware, RLS everywhere, gateway auth + missing routes, the approvals sensitivity retrofit, break-glass runtime, live frontend wiring, and honest CI/tests/docs.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

## Skills to activate
> `mersal-platform-architect`, `refugee-healthcare-management` (always-on), plus `healthcare-database-architect` (16.2/16.4), `healthcare-uiux-designer` (16.7). Index: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first
- [`../../docs/AUDIT-2026-07-26.md`](../../docs/AUDIT-2026-07-26.md) — findings C1–C3, H1–H10, bundled mediums. Authoritative for this phase.
- `docs/HANDOFF.md` + `docs/BUILD-STATUS.md` (machine gotchas: `./dotnet.sh`, PG :55432, .NET 8 only, analyzers-as-errors, CPM, pnpm).
- Reference implementations already in-tree: **provider-service** (RLS + interceptor + NOBYPASSRLS role + 2-role isolation test), **audit-service** (SqlFileMigrator, immutability), **claims-service** (test discipline).
- Run order = the gates below. One sub-prompt ≈ one commit. Full suite green after each: `./dotnet.sh test HbmpPlatform.sln -c Release` + `pnpm -r test`.

---

## GATE A — truth & safety

### 16.1 — Secrets purge + status-doc reconciliation (C2 + doc findings)
```text
1. SECRETS: rotate the hbmp_app DB password; remove the literal from docs/HANDOFF.md:115 (replace with
   "set out of band — OpenBao path secret/hbmp/db/app"); purge the old value from git history
   (git filter-repo) and add a gitleaks rule matching prose passwords (Dev_*_20\d\d pattern).
   Also remove the ConnectionStrings sections (change-me placeholders) from every service's BASE
   appsettings.json — connection strings come from env/OpenBao only; keep Development overrides in
   appsettings.Development.json. Replace all ~14 hardcoded "Password=hbmp" code fallbacks with
   `?? throw new InvalidOperationException(...)` (fail fast, never a baked credential).
2. DOC TRUTH (docs/BUILD-STATUS.md + docs/HANDOFF.md):
   - Tick 10b.1–10b.9 ☑ with the SHAs already in HANDOFF (0278c0b, e68a64e, 2323e2f, 735021a,
     6851e54, fc2d76a, 2bac316, acf86eb, 7dc6ea7).
   - Split row 11 into 11.1 ◐ (k6 suite + baseline landed, staging run pending), 11.2 ◐ (STRIDE/OWASP/
     sign-off landed — but downgrade the break-glass ✅ to 🟡 pending 16.6), 11.3 ☐.
   - Backfill the ~20 "(n.n)" commit placeholders from git log; reconcile the 0.1 SHA conflict
     (cac74b3 vs 8208331) against git log.
   - Rewrite the phase-14 dependency note in PAST tense: 14 landed as a retrofit over 7–10b; portals
     half done; approvals sensitivity retrofit NOT done — carried forward as 16.6 (explicit open item).
   - Rewrite HANDOFF header + §2/§3/§9a/§9b to current reality (19 services, ~700 tests, document-service
     complete); move stale sections under a "HISTORICAL — do not action" divider.
Acceptance: gitleaks passes incl. new rule; no ConnectionStrings in base appsettings; BUILD-STATUS and
HANDOFF agree with git log and with each other; grep for the old password returns nothing.
```

### 16.2 — Durable transactional outbox (C1 — the big one)
```text
Replace the in-memory outbox with a real transactional outbox, preserving the existing IOutbox API so
services don't change their call sites. Read libs/events/* and audit-service's SqlFileMigrator first.
- Add libs/events/EfOutbox: outbox_message table per service schema (id uuid, topic, type, payload jsonb,
  occurred_at, dispatched_at NULL, attempts, correlation_id), UNIQUE(id); EnqueueAsync writes it in the
  SAME DbContext/transaction as the domain mutation (accept the caller's DbContext — this is the point).
- OutboxRelayService reads undispatched rows (FOR UPDATE SKIP LOCKED, batched), publishes to RabbitMQ,
  marks dispatched_at; retries with backoff; poison rows quarantined after N attempts + alert log.
- AddHbmpEvents(useInMemory:) becomes environment-driven: InMemory ONLY when env=Development AND
  explicitly configured; default = durable. Add the outbox_message migration to every service
  (additive; use one shared SQL template).
- Migrate audit emission first (the audit spine must be atomic with mutations), then domain events.
Acceptance (per representative service — patient, orders, approvals, claims):
- Given a mutation commits, Then the outbox row is in the same transaction (kill the relay, restart,
  event still delivered exactly once — consumer dedupe already exists).
- Given the process crashes after commit but before publish, Then the event is delivered on restart.
- Given the broker is down, Then mutations still commit and rows drain when it returns.
Tests: a durability integration test in libs/events + one per representative service; full suite green.
ADR-0013 "Durable outbox (supersedes in-memory interim)".
```

### 16.3 — Admin authorization middleware + idempotency (C3)
```text
services/admin: add .RequireAuthorization(HbmpPolicies.Scope("admin:read"|"admin:write")) to EVERY
MapGroup (Users, Platform, PolicyConfig, AccessReview, Governance, BranchAssignment — keep /me on its
existing self-service policy). Keep AdminGate as the second layer (defense in depth), not the only one.
Add the admin:read/admin:write scopes to infra/keycloak (realm + scope-catalog) and require MFA via the
existing ScopeRequirement path. Add Idempotency-Key (processed_request ledger pattern from emr) to all
admin mutations (role grant/revoke, de-provision, tenant upsert, break-glass lifecycle, policy/config
writes) and to document upload + finance/reporting mutations flagged in the audit.
Acceptance: unauthenticated → 401 at middleware (handler never runs); token without admin scope → 403;
token without MFA (acr/amr) → 403; replayed Idempotency-Key on role-grant → single grant. Tests: an
AdminAuthnTests matrix + idempotent-replay tests; full suite green.
```

## GATE B — engage the defenses

### 16.4 — RLS everywhere (H1)
```text
Do what provider-service already did, everywhere. Read services/provider/Infrastructure/Rls.cs,
0003_rls.sql, 0004_app_role.sql, RlsIsolationTests.cs first.
1. Lift RlsConnectionInterceptor + RlsContext into libs/ (e.g. libs/authz/Rls or a small libs/data);
   provider consumes the shared copy (delete its local one).
2. Switch ALL runtime connection strings (compose + appsettings + code) to the hbmp_app NOBYPASSRLS
   role; hbmp (owner) is used ONLY by the migration path. Do this in the same commit as (3) per service
   to avoid the deny-all trap the audit predicted (policies read GUCs that nothing sets).
3. Register the interceptor in every service with a DbContext; bind app.tenant_id (+ provider/branch
   GUCs where policies use them) per request from the principal.
4. Add tenant RLS migrations (ENABLE + FORCE + policies on current_setting('app.tenant_id')) to the
   PHI-first services in order: patient, emr, document, case, then eligibility, orders, pharmacy,
   policy, callcentre, finance, approvals, notification, reporting.
Acceptance: per service, a two-role isolation test in the RlsIsolationTests style proves (a) hbmp_app
without the GUC sees zero rows, (b) with tenant A's GUC sees only A, (c) migrations still apply as hbmp.
CI: the test job must run these (see 16.8). ADR-0011 "hbmp_app NOBYPASSRLS runtime role".
```

### 16.5 — Gateway auth, missing routes, transport hardening (H3, H8, part of H10)
```text
KONG (infra/compose/config/kong.yml):
- Add the JWT/OIDC plugin globally, validating against the Keycloak realm JWKS; except health/metrics
  routes. Services keep their own validation (defense in depth, unchanged).
- Add missing routes: /api/v1/branches + /api/v1/practitioners + /api/v1/specialties (provider-service),
  /api/v1/me (admin-service), /api/v1/report-access-requests|grants (orders-service),
  /api/v1/examination-types (masterdata), /api/v1/documents (document-service).
- Fix the 3-way /api/v1/beneficiaries collision: add higher-specificity routes so
  /beneficiaries/{id}/allergies + /medication-history → emr and /beneficiaries/{id}/coverage-summary →
  eligibility, before the patient-service catch-all. Add If-Match to CORS allow-list and ETag to the
  expose-list; set CORS credentials:false.
- CI guard: a script that extracts every MapGroup prefix from services/*/Api and fails if kong.yml lacks
  a route for it (wire into the pipeline in 16.8).
TRANSPORT:
- Delete RequireHttpsMetadata:false from every BASE appsettings.json (keep only in Development).
- Bind every datastore/console port in compose to 127.0.0.1: (Postgres, MinIO, RabbitMQ, NATS, Valkey,
  OpenSearch, OpenBao, Prometheus, Grafana) — Kong stays public.
- Flip OPENSEARCH_DISABLE_SECURITY default to "false" (opt-in true for dev only).
- Add UseHsts + UseHttpsRedirection outside Development in the service template + all Program.cs.
- Remove services/hello from sln/compose/kong (or move to tools/) — no demo endpoint in prod topology.
Acceptance: unauthenticated request through Kong → 401 at the edge; the route-coverage CI script passes;
branch switcher + report-access + documents reachable through the gateway; compose up exposes only
Kong + web ports on 0.0.0.0.
```

### 16.6 — Sensitivity retrofit, break-glass runtime, projection, document fixes (H2, H4, H5, H9 + mediums)
```text
1. APPROVALS SENSITIVITY (the privacy hole — design ../37 §6 is authoritative): the approvals /review
   projection and the emr oversight path (EmrPolicies.ReadOversight → ClinicalGate) must return
   EXISTENCE METADATA ONLY (category, date, status, branch, RESTRICTED marker) for any result with
   sensitivity_level != 'Standard' — no values, no report refs — unless an active report_access_grant
   exists for the caller. Add ApprovalsSensitivityTests proving a MentalHealth result yields
   metadata-only to medical_approval; sanity-assert case/finance/claims see nothing new.
2. BREAK-GLASS RUNTIME (H5): implement HttpBreakGlassProvider in libs/authz — queries admin-service
   /break-glass/active for the principal (short TTL cache ~30s, FAIL-CLOSED on error), registered in
   every service (replaces NullBreakGlassProvider). Cross-service test: grant in admin → elevated read
   in emr with HIGH-severity audit → expiry ends it. Downgrade docs/compliance/security-sign-off.md
   SEC-BREAKGLASS to ✅ only after this test is green.
3. FIELDPROJECTOR ADOPTION (H2): route PHI list/read responses through libs/authz FieldProjector in
   patient, emr, document (first wave): declare field classes per DTO, project server-side, add a
   reflection min-necessary test per service (reception/finance/callcentre cannot receive clinical
   fields). Keep hand-built DTOs only where they already equal the projected shape.
4. DOCUMENT-SERVICE (H9): row-scope GET /beneficiaries/{id}/documents via the authz engine (tenant +
   role + beneficiary relationship), emit an audit Read event, introduce document:read and patient:read
   scopes and split read/write groups (also in patient-service). 5. PATIENT PATCH AUDIT: add the missing
   audit emit on PATCH /registrations/{id}. 6. MASTERDATA AUDIT: AddHbmpAuditClient + audit the
   screening endpoints. 7. GATE DEDUP: extract the shared 401/403 problem-shape into libs/authz
   GateResults and refactor the ~14 *Gate.cs files onto it (behavior identical, one problem shape).
   8. PHARMACY SQL: replace the interpolated sequence-table name with a switch over two constants.
Acceptance: all listed tests green + existing 700 unchanged; grep FieldProjector over services/ now
matches patient/emr/document; sign-off updated honestly. ADR-0012 "sensitive-result grants", ADR-0010
"consume/dispense executor invariant" (retro-document), ADR-0014 "phase-14 retrofit scope".
```

## GATE C — connect & prove

### 16.7 — Live frontend wiring (H6, H10 + frontend mediums)
```text
IDENTITY/SCOPES:
- Mint every scope in infra/keycloak/scope-catalog.yaml as a client scope; add all to hbmp-web
  optionalClientScopes; rename reception:search→reception:read in config.ts; add the call_center realm
  role + callcentre:interaction/verify/read/act scopes; map call_center in ROLE_MAP.
- FAIL CLOSED: roleFromRealmRoles returns null → render a "no portal assigned" page (never default
  reception). Keep the token IN MEMORY ONLY (remove the sessionStorage mirror; restore via prompt=none
  silent re-auth); "keep alive" must run a real refresh/silent re-auth and derive expiresAt from exp.
  Exclude DevAuthClient + DevApiClient from production bundles (build-time conditional, not runtime).
LIVE PATH:
- Add apps/web/.env.example (VITE_LIVE=1, VITE_API_BASE, VITE_OIDC_*) + document in README; add a CI
  live smoke job (compose up core + Kong, login via password grant on a test user, hit /reception/search).
- Fix useBranchContext double /api/v1 prefix; route CallCentre + branch hooks through ApiProvider
  (add their methods to ApiClient with Dev implementations) so fixture mode covers them.
- ERROR CONTRACT: http.ts parses problem+json into ApiError{status,title,detail,type}; AsyncSection
  branches 401→re-auth, 403→forbidden page, 409/412→refresh-and-retry affordance; stop swallowing the
  approvals /assign failure (only ignore 409-already-mine); CallCentre history via useAsync (no
  render-phase fetch, error ≠ empty); implement the CallCentre reschedule action with the same
  conflict handling.
- IDEMPOTENCY: mint Idempotency-Key once per form instance (useRef), rotate only after confirmed
  success (ApprovalsWorklist, PharmacyDispense, LabQueue, CallCentre book).
- MIN-NECESSARY LIVE: send scope to /dashboards/executive and filter server-side in reporting-service
  (the finance-no-diagnoses rule must not live only in fixtures); bind finance export from/to to real
  date inputs.
- SENSITIVITY UI: mount RestrictedResultCard/RequestAccessDialog in the results screens (branch on
  restricted===true), rebuilt from design-system components (StatusChip kind="neu" + Icon + Button —
  delete the non-token inline styles/emoji).
- I18N: consolidate the per-file dictionaries into en.json/ar.json behind react-i18next (already a
  dependency); add bilingual label maps for all enum codes (reason/outcome/purpose/priority/status);
  wire ThemeProvider.onLangChange to i18n.changeLanguage.
- CLAIMS UI DECISION: add a minimal claims_officer portal (worklist + batch + decision screens against
  the existing endpoints) OR record an explicit ADR that claims is API-only for v1 — no silent gap.
Acceptance: live login succeeds against the realm (invalid_scope gone); call-centre agent logs in and
completes the E2E flow through Kong; unknown role → no-portal page; token survives refresh via silent
re-auth only; axe green; fixture mode still fully works.
```

### 16.8 — CI & test integrity (H7 + quality mediums)
```text
1. SKIPPABLE, NOT SILENT: create libs/testing (PostgresFixture + SkippableDbCollection using
   Xunit.SkippableFact); replace all 165 `if (Db is null) return;` early-returns so unconfigured DB
   tests report SKIPPED. Reference from all 35 integration/concurrency test files (delete the duplicated
   DbCollection.cs copies).
2. CI DATABASE: add postgres:16 as a service to the test job with per-service *_TEST_DB vars so the
   concurrency + RLS + integration proofs actually run. Fail the pipeline if >0 skipped DB tests in CI.
3. COVERAGE GATE: coverlet Threshold=80 (line) on Domain projects in Directory.Build.props Tests group;
   remove the echo.
4. ONE CI: consolidate on GitHub Actions (backend-ci.yml: restore/build/test+coverage with the PG
   service; keep frontend-ci, perf-ci, security-ci) and reduce .gitlab-ci.yml to a mirror or delete it
   (record the choice in ADR-0001 amendment). Wire the a11y job to the real pnpm test:a11y; replace the
   package-stage echo with a docker build matrix over services/*/Dockerfile + cosign sign.
5. ESLINT: flat config with @typescript-eslint/no-explicit-any:error + jsx-a11y + react-hooks;
   lint = tsc --noEmit && eslint .; then burn down HttpApiClient.ts's 129 `any` by zod-parsing at every
   getRaw/postRaw boundary (the schemas already exist in libs/contracts).
6. AXE BREADTH: parameterize the a11y test over every SCREENS entry (all portals incl. CallCentre);
   re-enable color-contrast via a Playwright axe run if jsdom can't.
7. OPENAPI ARTIFACTS: emit services/<name>/openapi.json at build, commit them, CI drift check;
   plan (not necessarily execute) generating @mersal/contracts from them.
8. MIGRATION RUNNER: promote audit's SqlFileMigrator to libs/testing; test fixtures apply
   Migrations/*.sql to a throwaway schema (catches entity↔DDL drift); add the missing
   CopyToOutputDirectory ItemGroup to the 8 Infrastructure csprojs lacking it.
9. TEST DEBT: add DocumentAuthzTests (cross-tenant/beneficiary denial + storage integration),
   MasterDataAuthzTests (write-gating), component tests for RestrictedResultCard/ReportView/LabQueue/
   PharmacyDispense redacted states; add an eligibility consumer health-check (degraded when broker
   down) with a test.
Acceptance: CI shows real numbers (tests run, none silently skipped, coverage enforced ≥80% on domain);
route-coverage + OpenAPI drift + gitleaks + eslint + axe all gating; one authoritative pipeline.
```

## GATE D — polish

### 16.9 — Small items
```text
- ADRs 0010–0014 if not already written in 16.2/16.4/16.6 (consume/dispense invariant, hbmp_app role,
  sensitive-result grants, durable outbox, phase-14 retrofit scope).
- Masterdata loader: persist load-report.json + env-gated MasterDataLoadTests asserting floor counts
  (ICD ≥16,751, CPT ≥10,810, ATC ≥2,150, drugs ≥25,063).
- Approve-rationale decision: require rationale on plain approve OR amend README/BUILD-STATUS to
  "mandatory on reject/partial/info-request" — pick one, implement, document.
- Problem+json sweep: replace bare Results.NotFound()/BadRequest(anonymous) with Results.Problem(...)
  in masterdata, policy, eligibility, approvals, claims, finance.
- .gitignore: drop the over-broad [Rr]elease/ line (bin/ + obj/ already cover .NET).
- docs/runbooks/ remains phase 11.3 scope — do NOT do it here; just ensure BUILD-STATUS shows 11.3 as
  the only open half of 11.
Acceptance: all merged, suite green, BUILD-STATUS/AUDIT updated with completion notes per finding id.
```

---

## Guardrails
- **Never weaken a control to make a test pass.** Every fix is additive enforcement.
- RLS switch and GUC-interceptor registration land **together per service** (avoid the deny-all trap).
- Preserve the consume/dispense/booking invariants byte-for-byte — re-run their suites after every gate.
- Additive migrations only; no behavior change to green endpoints except where the audit names them.
- Update `docs/AUDIT-2026-07-26.md` with a ✅/date per finding id as it closes; BUILD-STATUS gains a
  phase-16 row per sub-prompt.

## Done when
- [ ] C1–C3 closed: durable outbox platform-wide, credential rotated + purged, admin behind middleware+MFA.
- [ ] H1–H10 closed: RLS live as `hbmp_app` in all PHI services with 2-role tests; FieldProjector adopted (first wave); Kong authenticates + all routes present + collision fixed; approvals returns metadata-only for sensitive results; break-glass elevates with fail-closed provider; live login works incl. call-centre; CI runs real DB tests with enforced coverage; transport hardened; document-service scoped + audited; live wiring proven by a smoke test.
- [ ] Status docs (BUILD-STATUS, HANDOFF, security sign-off, AUDIT) are true statements about the code.
- [ ] Full backend + frontend suites green; no silently-skipped tests in CI.
