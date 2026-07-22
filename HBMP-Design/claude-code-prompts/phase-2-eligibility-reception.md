# Phase 2 — Eligibility Engine, Reception Search & Visit Gating

**Goal:** Build `eligibility-service` (Eligible/Ineligible/NeedsAuthorization with cached snapshots), a **minimum-necessary** reception search backed by OpenSearch, and status-driven visit gating that only lets **Active** members start an encounter. (Release **R1**)

Back to [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md).

---

## Skills to activate
> Activate `policy-eligibility-engine`, `health-insurance-tpa-operations`, `healthcare-uiux-designer` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- `../07-functional-requirements.md` — R1 eligibility, reception, and visit-gating requirements.
- `../11-permission-matrix.md` — **field-level** min-necessary rules: Reception ≠ EMR/diagnoses. This is the authority for what the result card may and may not expose.
- `../13-ux-flows.md` — reception search → result card → check-in / gate flow.
- `../15-database-erd.md` §5 (`eligibility_snapshot`, `coverage_limit`) and §7 (`encounter`, `appointment`).
- `../17-api-specifications.md` §5 (Eligibility) and §6 (Encounters) — request/response shapes.
- `../23-state-machines.md` §1 (member statuses) and §6 (appointment/encounter) — gating conditions.
- `../32-user-stories.md` — **US-010** (reception search) and **US-011** (status-driven visit gating).

Root `CLAUDE.md` governs stack, security, audit, a11y, testing, and Definition of Done — not repeated here.

---

## Prompts

### 2.1 — `eligibility-service`: compute decision + cache snapshot + event-driven invalidation

```text
Implement eligibility-service (.NET 8) owning schema `eligibility`, per ../15-database-erd.md §5,
../17-api-specifications.md §5, and ../23-state-machines.md §1.

Decision engine — POST /api/v1/eligibility/check { beneficiaryId, benefitCategory, serviceCode? } returns
{ decision, coverageId, reasons[], limitState{ limitType, limitValue, consumedValue, remaining },
snapshotExpiresAt }. Compute decision from THREE inputs:
- Member status (from patient-service events): only Active can be Eligible; Expired/Suspended/Blocked/Inactive
  → Ineligible with a reason. Pending → Ineligible.
- Policy/coverage validity (effective dates + coverage status) for the requested benefit category.
- Remaining limits: remaining = limit_value - consumed_value across the applicable coverage_limit rows.
  If a gated service or remaining insufficient/service requires pre-auth → NeedsAuthorization (not a hard No).
Decision domain is EXACTLY {Eligible, Ineligible, NeedsAuthorization}.

Snapshot + cache:
- Persist eligibility_snapshot (decision, denormalized limit_state jsonb, computed_at, expires_at,
  version_hash). Treat it as a derived read model — NOT a source of truth.
- Cache-first in Valkey keyed by (beneficiaryId, coverageId/benefitCategory); serve from cache within TTL.
- Build the initial snapshot on `BeneficiaryActivated` (from phase 1).
- INVALIDATE the cache + recompute on PolicyChanged, CoverageChanged, CoverageLimitChanged, and any member
  status event (Suspended/Expired/Blocked/Inactive/Reactivated). Consumers are idempotent (dedupe on event id).

Acceptance:
- Given an Active member with a valid coverage and remaining > 0, When I check, Then decision = Eligible with
  limitState populated.
- Given a Suspended/Expired/Blocked/Inactive member, When I check, Then decision = Ineligible with a reason.
- Given a gated service or insufficient remaining, When I check, Then decision = NeedsAuthorization.
- Given a policy/coverage/status change event, When it is processed, Then the cached snapshot is invalidated
  and the next check recomputes.

Non-functional: p95 < 2s for a check (cache-first path well under). Tests: unit (decision matrix across the
three inputs), integration (cache hit/miss + event invalidation), and an audit assertion — every eligibility
READ writes an audit event (PHI read). Update OpenAPI + README.
```

### 2.2 — Reception search: OpenSearch index of min-necessary fields + result-card DTO

```text
Implement reception search per ../11-permission-matrix.md, ../13-ux-flows.md, and US-010. The reception role
must be able to confirm eligibility fast WITHOUT ever seeing clinical/EMR data.

Search index (OpenSearch):
- Index ONLY minimum-necessary fields: memberNo, given/family name, identifiers (NationalID/Passport/
  RefugeeID/UNHCRNo/MemberNo), policyNo, primary phone, member status, and denormalized coverage/limit summary.
- Do NOT index diagnoses, notes, orders, prescriptions, vitals, or any EMR field.
- Keep the index in sync via domain events (BeneficiaryRegistered/Activated/Updated, Policy/CoverageChanged).

Search endpoint — GET /api/v1/reception/search?q= supporting lookup by NationalID / Passport / Card(memberNo)
/ Policy / Phone. Returns a ReceptionResultCard DTO exposing ONLY:
- identity: memberNo, display name, member status (with non-color status semantics for the UI);
- coverage: active benefit categories;
- remaining limits: limitType + remaining per category;
- visitHistorySummary: count + last-visit date/type ONLY — NO diagnoses, notes, results, or medications.

Enforce field-level authorization server-side: the reception scope cannot request or receive EMR fields even
via query manipulation; projection happens on the server, not the client.

Acceptance (US-010):
- Given a valid identifier, When Reception searches, Then a result card with status, coverage, remaining
  limits, and a visit-history SUMMARY returns within 2s (p95).
- Given no match, When Reception searches, Then an empty state suggests trying another identifier or
  registration.
- Given the Reception role, When the card renders, Then no EMR/diagnosis data is present in the payload.

Tests: integration (search by each identifier type), an AUTHORIZATION test proving the reception DTO/endpoint
never returns EMR fields (attempt to select diagnosis → denied/omitted), and a p95 latency check. Audit every
search/read. Update OpenAPI + README.
```

### 2.3 — Visit gating + encounter creation stub

```text
Implement status-driven visit gating and an encounter-creation stub in emr-service, per
../23-state-machines.md §1 & §6, ../17-api-specifications.md §6, and US-011.

Behavior:
- On "create visit / check-in", first call eligibility (2.1) / read member status.
- BLOCK visit/encounter creation when status ∈ {Expired, Suspended, Blocked, Inactive} (and Pending): return
  RFC 7807 409/422 with actionable guidance (e.g., "refer to Case Manager"). No encounter is created.
- ALLOW when status = Active: create an encounter (encounterNo ENC-*, status InProgress) via
  POST /api/v1/encounters, mark the appointment CheckedIn where one exists, and add the beneficiary to the
  clinician queue/worklist so they appear for the doctor.
- This is a STUB: full SOAP/diagnosis/orders come in phase 4 — create only the encounter shell + queue entry.

Acceptance (US-011):
- Given Expired/Suspended/Blocked/Inactive, When Reception tries to create a visit, Then it is blocked with
  guidance and nothing is persisted.
- Given Active, When Reception proceeds, Then an encounter is created and the patient appears in the clinician
  queue.

Tests: integration parameterized over every status (blocked vs allowed), an audit assertion on both the gate
decision and the encounter creation, and an idempotency check on encounter creation. Emit `ApptCheckedIn` /
`EncounterStarted` events. Update OpenAPI + README.
```

---

## Guardrails

- **Reception cannot access EMR.** The result card and the search index carry no diagnoses/notes/orders/rx/vitals. Add an explicit **authorization test** proving reception requests for EMR fields are denied/omitted (`../11-permission-matrix.md`).
- **Every eligibility read is audited.** Eligibility checks are PHI reads — write an `audit_event` for each, via `libs/audit-client`.
- **Snapshots are derived, never authoritative.** `coverage_limit.consumed_value` in policy-service remains the source of truth; snapshots are cache/read-optimized and must be invalidated on policy/coverage/status events.
- **Performance.** p95 < 2s for reception search and eligibility check; serve cache-first and measure it.
- **Minimum-necessary projection is server-side**, not a client filter — the payload itself must exclude disallowed fields.
- **Idempotent event consumers** (dedupe on event id) and **idempotent encounter creation**.

## Done when

- `POST /eligibility/check` returns exactly **Eligible / Ineligible / NeedsAuthorization** from member status + policy/coverage + remaining limits, cached in Valkey and **invalidated by policy/coverage/status events**.
- Reception search returns a **minimum-necessary result card** (status, coverage, remaining limits, visit-history summary) by ID/Passport/Card/Policy/Phone within **p95 < 2s**, with an authz test proving **no EMR leakage**.
- Visit creation is **gated by status**: blocked for Expired/Suspended/Blocked/Inactive with guidance; for Active it creates an encounter + queue entry.
- All eligibility/search reads and gate decisions are audited, and unit/integration/authz/latency tests are green — meeting the root `CLAUDE.md` Definition of Done.
