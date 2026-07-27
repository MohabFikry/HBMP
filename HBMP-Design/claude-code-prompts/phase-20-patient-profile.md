# Phase 20 — Unified Patient Profile (role-projected 360)

**Goal:** One canonical patient profile — identity + photo, alerts, coverage & eligibility, past medical history, encounters, investigations & results, prescriptions, authorizations, referrals, documents, notes, financial, case, timeline, **call history** — reachable from search and every worklist, **projected server-side to exactly what the viewing role may see**, linked into every module, and fully audited.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> ⚠️ **This is the highest-risk feature in the platform.** Every other module is naturally scoped; this one deliberately aggregates everything about a patient onto one screen. Built naively — one fat payload filtered in the browser — it becomes the single bypass that simultaneously undoes reception ≠ EMR, finance ≠ diagnoses, labs ≠ prescriptions, pharmacy ≠ results, and the phase-14 sensitive-result gate. Read `../39-patient-profile.md` §1 before writing any code.
>
> **Sequencing:** after phase 18 Gate B (min-necessary projection + RLS engaged) and phase 19.3b/19.3c (documents + timeline), since the profile surfaces both. Phase 15 call-centre and phase 14 sensitivity must already be in place — the profile consumes their gates, it does not re-implement them.

## Skills to activate
> `mersal-platform-architect`, `refugee-healthcare-management` (always-on), plus `clinical-workflow-designer`, `patient-journey-designer`, `healthcare-uiux-designer`, `policy-eligibility-engine`. Index: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first
- [`../39-patient-profile.md`](../39-patient-profile.md) — **AUTHORITATIVE**: sections (§3), role×section matrix (§4), photo rules (§5), **call history + copy-summary rules (§5b)**, invariants (§7).
- [`../11-permission-matrix.md`](../11-permission-matrix.md) (min-necessary + field classes) · [`../37-branch-scoping-and-clinical-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md) §3/§6 (branch scope, sensitive gate) · [`../38-policy-member-administration.md`](../38-policy-member-administration.md) §5/§5b/§5c (notes, documents, timeline) · [`../19-audit-strategy.md`](../19-audit-strategy.md) · [`../0B-DESIGN-SYSTEM-UI.md`](../0B-DESIGN-SYSTEM-UI.md) + [`../21-accessibility-checklist.md`](../21-accessibility-checklist.md).
- **Existing code — you are CONSOLIDATING these, not adding a fifth:** `services/case` beneficiary-360, `services/callcentre/Api/Members.cs` (verification-gated 360), `services/emr/Api/ClinicalRecords.cs` (clinical context + FieldProjector usage), the phase-19 administrative 360. Also `libs/authz` (`FieldProjector`, `RowScope`, `IAuthorizationEngine`, `BreakGlass`), `libs/auth` (`IBranchContext`), `services/document` (blob + ClamAV + signed URLs), `apps/web/src/{portals/catalog.ts,screens/registry.tsx,api/HttpApiClient.ts}`.
- `docs/HANDOFF.md` gotchas (`./dotnet.sh`, PG :55432, .NET 8 only, analyzers-as-errors, CPM, pnpm).

## THE INVARIANTS
1. **Server-side projection only.** A withheld field is **absent from the JSON** — never hidden by CSS, never present-but-unrendered. Proven by reflection tests over the serialized payload.
2. **Compose under the CALLER'S token. Never a service account.** A privileged aggregator that fetches everything then filters is the classic aggregation vulnerability. Each owning service still applies its own authz; profile section-gating is a second, independent layer.
3. **The profile is an INTERSECTION of existing rules, never a union.** Treating relationship, provider ownership, branch scope, payer scope, call-centre verification and sensitive-result grants all still bind. It weakens no gate and grants no new access.
4. **It owns no data** — pure composition. A local copy of clinical data here would be a second source of truth.
5. **Every open is an audited PHI read** recording which sections were actually served; withheld sections are logged as withheld.
6. Restricted / unavailable / empty are **three distinct states**, never collapsed.

---

## Prompts

### 20.1 — `profile-service`: the canonical contract + composition engine
```text
Create services/profile (.NET 8) — a COMPOSITION service that owns NO domain data and has NO DbContext
for clinical/benefit entities (it may own only a small projection-cache table if 20.5 needs it).
Read ../39 §3/§4/§7 first, plus the four existing 360s you are consolidating.

CONTRACT (libs/contracts + OpenAPI)
- GET /api/v1/patients/{beneficiaryId}/profile?sections=<csv>  → PatientProfile
- PatientProfile = { beneficiaryId, servedAt, sections: ProfileSection[] }
- ProfileSection = { key, state: 'Visible'|'Restricted'|'NotApplicable'|'Unavailable',
                     reasonCode?, requestAccessAction?, data? }
  * Visible      → data present
  * Restricted   → data ABSENT + reasonCode (e.g. 'sensitive-requires-grant', 'not-treating',
                   'role-not-permitted') + optional requestAccessAction
  * NotApplicable → nothing exists for this patient
  * Unavailable  → the owning service failed/timed out (NOT the same as empty — see below)
- 15 section keys exactly per ../39 §3: header, alerts, coverage, pastMedicalHistory, encounters,
  investigations, prescriptions, authorizations, referrals, documents, notes, financial, caseManagement,
  timeline, callHistory.
- Rows that offer a clipboard action carry a server-generated `copyText` string (see 20.3b). The client
  copies what it was GIVEN — it never assembles clipboard text from fields it holds.

COMPOSITION ENGINE
- One ISectionProvider per section, each calling its OWNING service over HTTP **forwarding the caller's
  bearer token** (and X-Active-Branch). Register them; the engine fans out in PARALLEL with a per-call
  timeout and an overall budget.
- FORBIDDEN: any client credential / service-account path in this service. Add an architecture test that
  fails the build if the profile HTTP clients can resolve a client_credentials token handler.
- A provider that throws/times out yields state='Unavailable' — the profile still returns 200 with the
  other sections. Never fail the whole profile for one section; never report a failure as empty.
- Section gating BEFORE fetch where the role can never see it (skip the call entirely — cheaper and
  leaks nothing), and AFTER fetch via FieldProjector for field-level trimming.
- ?sections= lets callers request a subset (the context bar needs only header+alerts) — the response
  still honours the matrix regardless of what was asked for.

AUTHZ
- Build ProfilePolicies in libs/authz: one rule per (role, section) from ../39 §4, with the existing
  conditions (TenantMatch, TreatingRelationship, ProviderOwnership, BranchScope, CaseAssignment,
  SensitiveGrantActive, CallCentreVerified). NO new condition types unless ../39 names one.
- Reception/CallCentre: clinical sections are never fetched. Lab: investigations filtered to its own
  orders. Pharmacy: prescriptions filtered to its own. Non-treating doctor: everything clinical is
  Restricted, not absent — so they can request access rather than assume nothing exists.
- Medical Approval sees clinical sections BUT sensitive results stay existence-only until a ../37 §6
  grant exists. The profile MUST NOT be a shortcut around that gate.
- callHistory projects at THREE levels per ../39 §5b — Full / Operational / Meta — not just visible/
  hidden. Meta (finance/claims) carries NO summary text; Operational carries the summary but no
  verification detail and no agent notes; only Full sees verification detail. Model this as a
  FieldProjector class, not as three DTOs.

AUDIT
- One ProfileViewed event per open: actor, beneficiaryId, sections served (keys + states), purpose if
  supplied, correlationId. Restricted sections recorded as withheld. This is what makes "who looked at
  this patient" answerable — it is not optional.

ACCEPTANCE (Given/When/Then)
- Given a reception principal, When the profile is fetched, Then investigations/prescriptions/PMH are
  absent or Restricted and NO clinical field appears anywhere in the serialized JSON.
- Given a lab principal, Then investigations contains ONLY that provider's orders.
- Given a non-treating doctor, Then clinical sections are Restricted with reasonCode 'not-treating'.
- Given a mental-health result and an approvals principal with no grant, Then investigations shows
  existence metadata only.
- Given a finance principal, Then callHistory rows contain direction/date/reason/outcome and NO summary
  text anywhere in the serialized JSON.
- Given emr is down, Then encounters is 'Unavailable' and the rest of the profile still renders.
- Given any open, Then exactly one audit event lists the served sections.
TESTS: role × section matrix as a table-driven reflection test over the SERIALIZED payload (15 sections
× every role); provider-ownership filtering; sensitive-gate integration; degradation (one provider
throws → Unavailable, others fine); the architecture test forbidding service-account composition;
audit assertion incl. withheld sections.
```

### 20.2 — Consolidate the four existing 360s onto the contract
```text
../39 §2: four partial 360s already exist and are diverging. A fifth would guarantee drift. Re-point
them; do not leave parallel shapes alive.

- services/case beneficiary-360 → becomes the 'caseManagement' section provider; the case portal calls
  the profile endpoint with ?sections=header,alerts,coverage,caseManagement,notes,timeline.
- services/callcentre call_interaction log → becomes the 'callHistory' section provider (see 20.3b).
  The call-centre portal's own call list keeps its existing agent/supervisor scoping — the profile
  section is a SECOND, narrower projection of the same rows, never a copy of them.
- services/callcentre Members 360 → becomes the call-centre PROJECTION of the profile. The verification
  gate stays exactly where it is (phase 15): unverified → the profile endpoint returns 403. Keep the
  callcentre endpoint as a thin facade that enforces verification then delegates, OR have the SPA call
  the profile directly with the verification context — choose, and record it in the ADR.
- phase-19 administrative 360 → becomes header + coverage + documents + notes + timeline sections.
- emr clinical context → becomes pastMedicalHistory + encounters + investigations + prescriptions
  providers (it already uses FieldProjector — reuse that logic, do not fork it).

RULES: no behaviour change for existing callers (their payloads may be a subset of the new shape, but
must not lose a field they legitimately received); delete the superseded bespoke DTOs once callers move;
every existing 360 test must still pass or be consciously updated with a note saying why.
ACCEPTANCE: grep shows no second aggregation path; each old endpoint either delegates or is gone;
existing suites green.
```

### 20.3 — Beneficiary photo (consent-gated, sensitive)
```text
Read ../39 §5 — treat the photo as identity-sensitive, not decorative. Reuse services/document; do NOT
add a second blob path.

- Capture/upload via document-service with document_class 'IdentityPhoto' (add to the phase-19.3b
  enum), visibility_class 'Administrative' but with its own explicit role allow-list (see below).
- CONSENT: an IdentityPhoto may only be stored when a recorded consent document exists for the
  beneficiary (ConsentForm covering photography/identification). Refusal is permitted and MUST NOT block
  registration or care — the profile simply shows an initials avatar.
- Serve as a SHORT-TTL SIGNED THUMBNAIL via GET /patients/{id}/photo (redirect or stream) — never a
  permanent or guessable URL, never a public bucket.
- ROLE ALLOW-LIST: reception, call centre, clinicians (treating), beneficiary management. **Finance,
  claims, labs, pharmacies and admin do NOT receive it** — the header section omits the photo field
  entirely for them.
- Versioned on replacement (old retained per retention); every retrieval audited; EXCLUDED from
  exports/extracts (phase 19.5b), notifications, and the FHIR façade.
ACCEPTANCE: no consent → upload rejected 422; finance/lab principals get a header with NO photo field;
retrieval audited; the signed URL expires; replacing creates a version.
TESTS: consent gate, role allow-list over the serialized header, TTL expiry, audit, export exclusion
(assert the extract engine cannot emit a photo column).
```

### 20.3b — Call history section: summary, direction, and copyable text
```text
Read ../39 §5b. The rows ALREADY EXIST — call_interaction in services/callcentre (phase 15). You are
adding a summary field, a projection, and a server-generated clipboard string. Do NOT create a second
call log, and do NOT move call data into services/profile.

SCHEMA (callcentre migration)
- ADD call_interaction.summary varchar(500) NULL — SEPARATE from the existing notes column. This split
  is the whole point: widening the audience for call history must not silently widen the audience for
  whatever the agent typed mid-call. `notes` stays agent-scoped and is NEVER promoted to other roles.
- summary is REQUIRED at close when outcome != 'Abandoned' (422 otherwise, with a clear message).
- Edits to summary go through the existing history/versioning path with an 'edited' marker + editor +
  timestamp. Never a silent overwrite.
- No new PII: identifier VALUES remain forbidden in this schema (phase 15 CRITICAL PRIVACY RULE).

READ API (consumed by the profile's callHistory provider)
- GET /api/v1/beneficiaries/{id}/call-interactions?level=full|operational|meta&page&pageSize
  Requires callcentre:history:read. The level is decided by the SERVER from the caller's role via
  ProfilePolicies — a client-supplied level may only NARROW, never widen (clamp it; do not trust it).
- Row: { callRef, direction: 'Inbound'|'Outbound', startedAt, endedAt, durationSeconds, branchCode,
         agentDisplayName?, reasonCode, outcome, verification?: {result, identifierTypes[]},
         summary?, linkedArtifacts: [{type:'Appointment'|'ContactChange'|'Complaint'|'FollowUp',
         ref, action}], copyText }
- Projection per ../39 §5b: meta drops summary + verification + agent + linkedArtifacts detail;
  operational drops verification + notes; full keeps everything. Implement with FieldProjector classes
  (CALLCENTRE_OPERATIONAL / CALLCENTRE_VERIFICATION), not three hand-written DTOs.

copyText — SERVER-GENERATED, FROM THE SERVED PROJECTION
- Build it from the SAME projected object that is serialized to the caller, in the same code path.
  If a field was projected away, it cannot appear in copyText — assert this with a test that projects a
  full row down to meta and greps copyText for the summary string.
- Format (../39 §5b), localized AR/EN, Africa/Cairo, always carrying provenance:
    [Outbound] 2026-07-24 14:32 (6m 12s) · Nasr City · Agent: R. Adel
    Member: MRS-M-014882 · Ref: CALL-2026-004137
    Reason: RescheduleAppointment · Outcome: Resolved
    <summary>
- NEVER include verification detail, agent notes, or any identifier value.
- Also expose POST /api/v1/beneficiaries/{id}/call-interactions/copy {callRefs[]} returning the joined
  block for "copy all visible" — same projection, one audit event.

AUDIT
- New event CallSummaryCopied { actor, beneficiaryId, callRefs[], level, correlationId }. Copying is the
  moment PHI leaves the platform's control, so it is logged like an export, not like a read. Emitted by
  the API that produces the clipboard block (a copy triggered purely client-side from an already-served
  row emits it via a small POST — do not skip it because "the data was already on screen").

GUARDRAIL FOR THE UI COPY (enforce in review): clinical content does not belong in a call summary.
Agents are not clinicians; a summary reading "complained of chest pain" creates an unreviewed clinical
record in an operational store. Surface this as helper text at the point of writing and route genuine
clinical escalation through the existing case/escalation path.

ACCEPTANCE
- Given close with outcome 'Resolved' and no summary, Then 422.
- Given a finance principal, Then rows have no summary and copyText contains no summary line.
- Given an operational principal, Then no verification block and no agent notes are present anywhere.
- Given a client sending level=full while holding an operational role, Then the response is operational.
- Given any copy, Then exactly one CallSummaryCopied event names the call refs.
- Given a summary edit, Then history retains the prior value and the row is marked edited.
TESTS: level clamping; projection→copyText derivation (the grep test above); required-summary rule;
edit history; audit on copy incl. copy-all; assert no identifier values persisted.
```

### 20.4 — Frontend: profile screen, context bar, search entry, module deep-links
```text
Read ../39 §6, ../0B (incl. §10b v1.1), ../14-navigation-structure.md, ../21, and the EXISTING
apps/web screens/registry.tsx + portals/catalog.ts.

PROFILE SCREEN (one component, role-driven — NOT one screen per role)
- Renders whatever sections the API returned, in ../39 §3 order. Alerts pinned directly under the
  header. The component must contain NO role logic beyond rendering states — the server decides.
- THREE DISTINCT STATES, visually and semantically different (this is a correctness requirement, not
  polish): Restricted = locked card, four-cue chip (neutral hue + lock icon + ghost pill + the word
  "Restricted"), reason text, and a "Request access" action where offered (wiring to the ../37 §6 flow
  for sensitive results). Unavailable = warning treatment + Retry. Empty = plain "No records".
  A user must never confuse "you may not see this", "it broke", and "there is nothing".
- Section navigation: sticky in-page nav (jump list) + collapsible sections remembering user preference.
- Deep-links INTO modules from each section, carrying the patient context: book appointment, start
  encounter, raise investigation order, new prescription, view authorization, open claim, upload
  document, add note. Every link permission-gated — render nothing rather than a link that 403s.
- PATIENT CONTEXT BAR: a compact strip (photo/initials, name, member no, age/sex, status chip, alert
  count) rendered in encounter, order, dispense, approval and call-centre screens, fetched with
  ?sections=header,alerts. Clicking opens the full profile. This is how a clinician knows which record
  they are in — treat it as a safety control.
- CALL HISTORY SECTION (../39 §5b): a reverse-chronological list. DIRECTION uses FOUR CUES — inbound
  and outbound differ by hue AND arrow icon (↙ in / ↗ out) AND chip shape AND the word — never colour
  alone (../0B four-cue rule). Show date/time (Africa/Cairo), duration, branch, reason, outcome chip,
  the summary, and linked artefacts as permission-gated deep-links (appointment → appointment screen,
  complaint → case). Filter by direction / reason / date range; paginate.
- COPY SUMMARY CONTROL: a real <button> (not a hover-only icon, not a bare glyph) on each row, with an
  accessible name that identifies the call — e.g. aria-label "Copy summary of outbound call on 24 July
  2026" — ≥44px, keyboard reachable in tab order, focus-visible. On activation it copies the row's
  SERVER-PROVIDED copyText (never a client-assembled string, never innerText scraped from the DOM),
  then announces success via aria-live ("Call summary copied") and shows a brief inline confirmation —
  do not rely on a toast alone. Provide a section-level "Copy all visible" using the copy endpoint.
  navigator.clipboard may be unavailable on http origins: fall back to a selectable read-only textarea
  in a dialog rather than failing silently. Mirror correctly in RTL (the arrow icons flip; the direction
  wording is translated, not transliterated).
- SEARCH → PROFILE: wire the existing search paths (reception, call centre, member query) so a result
  opens the profile. If the phase-18 command palette exists, patients are a result type there too.
- PRINT/EXPORT SUMMARY: generated SERVER-SIDE from the same projection (never from the rendered DOM),
  watermarked with viewer + timestamp, audited as a PHI export; it can never contain a section the
  viewer could not see on screen.
- Bilingual AR/EN with full RTL; ≥44px targets; keyboard: section jump list, Esc closes, focus visible;
  aria-live on section load outcomes; axe clean in EN and AR.

ACCEPTANCE
- Given a reception user, Then no clinical section renders AND none is present in the network payload
  (assert the payload, not the DOM).
- Given a restricted sensitive result, Then the locked state with "Request access" appears and wiring to
  the ../37 §6 request flow works end to end.
- Given emr is unavailable, Then that section shows Retry while others render.
- Given a clinician opens an encounter from the profile, Then the context bar shows the same patient.
- Given the print summary, Then it matches the on-screen projection exactly and is audited.
- Given a call row, When the copy button is activated by keyboard, Then the served copyText is on the
  clipboard, an aria-live confirmation is announced, and a CallSummaryCopied event is recorded.
- Given a finance user, Then the copied block contains no summary line.
TESTS: per-role component tests asserting the payload lacks withheld sections; three-state rendering;
deep-link permission gating; context-bar consistency; axe EN+AR; print-summary parity; call-direction
four-cue rendering (assert icon + text, not just class); copy button accessible-name + keyboard path +
clipboard content equals copyText; RTL mirroring of direction icons.
```

### 20.5 — Performance, routing, docs & rollout
```text
- PERFORMANCE: the profile fans out to ~8 services. Budget p95 < 2.5s for a full profile and < 400ms for
  ?sections=header,alerts (the context bar is on every clinical screen — it cannot be slow). Parallel
  fetch, per-call timeout, and a short-TTL (30–60s) per-(caller, patient, section) cache ONLY if needed —
  and if you cache, key it on the FULL authorization context (role, treating relationship, branch, grants),
  never on beneficiaryId alone. The phase-18 X9 lesson: never key a cache on fewer dimensions than the
  decision depends on. A stale cache that leaks a section across roles is a breach, not a bug.
- KONG: route /api/v1/patients/{id}/profile, /api/v1/patients/{id}/photo and
  /api/v1/beneficiaries/{id}/call-interactions (+ .../copy); add profile-service to compose; verify with
  the route-coverage guard (phase 18 E1). Add profile:read and callcentre:history:read scopes + role
  grants (finance/claims get the scope but resolve to the Meta level server-side).
- DOCS: ../11 gains the profile resource + the §4 section matrix as hard rules; ../14 gains the profile
  screen + context bar; ../16 gains profile-service (composition, owns no data); ../22 gains
  IdentityPhoto + call_interaction.summary; ../19 gains CallSummaryCopied in the audit event catalog;
  00-README-INDEX + README gain doc 39; BUILD-STATUS gains 20.1–20.5.
  ADR-0023 "Patient profile: server-side role projection, composed under the caller's token, owning no
  data" — record the rejected alternative (fat payload + client filtering) and WHY.
- ROLLOUT: ship read-only first, behind the existing portal permissions; verify the audit stream shows
  ProfileViewed with section lists before enabling the print/export summary.
ACCEPTANCE: performance budgets met on seeded volume; every route reachable through Kong; docs true;
ADR merged.
```

---

## Guardrails
- **No service-account composition. No client-side filtering. No new gate weakened.** These three are the phase.
- The profile **owns no data** — if you find yourself adding a clinical table to `services/profile`, stop.
- Sensitive results stay existence-only for everyone except the authoring/ordering clinician until a [37 §6](../37-branch-scoping-and-clinical-sensitivity.md) grant exists — including for the approval team.
- Photo is consent-gated, role-limited, signed-TTL, audited, and never exported.
- Clipboard text is **server-generated from the served projection and audited** — never assembled in the browser, never scraped from the DOM. Agent `notes` and verification detail never leave the call-centre role.
- Restricted ≠ Unavailable ≠ Empty — three states, always.
- Every profile open and every export writes an immutable audit event naming the sections served.
- Full suite green after each sub-prompt (`./dotnet.sh test HbmpPlatform.sln -c Release` + `pnpm -r test`).

## Done when
- [ ] One canonical **15-section** profile contract; the four existing partial 360s consolidated onto it and their bespoke shapes retired.
- [ ] Composition is server-side under the caller's token, with an **architecture test forbidding a service-account path**.
- [ ] The role × section matrix is enforced and proven by table-driven reflection tests over the **serialized payload** for every role — reception/call-centre see no clinical field, finance no diagnosis and no photo, lab only its own orders, pharmacy only its own Rx, non-treating doctor existence-only.
- [ ] Sensitive results remain existence-only **including for the approval team** without a grant; the "Request access" action wires to the phase-14 flow end to end.
- [ ] Photo: consent-gated, role allow-listed, short-TTL signed, versioned, audited, excluded from exports.
- [ ] Call history: inbound/outbound rendered with **four cues**; `summary` is a capped field distinct from agent `notes` and required at close; Full / Operational / Meta projection enforced server-side with client-supplied levels clamped, never widened.
- [ ] **`copyText` is server-generated from the served projection** — proven by a test that narrows a full row to Meta and finds no summary in the copied block; copying writes a `CallSummaryCopied` audit event; the copy control is a named, keyboard-reachable button with an aria-live confirmation and an RTL-correct direction icon.
- [ ] Restricted / Unavailable / Empty render as three distinct states; a failing section degrades without breaking the profile.
- [ ] Patient context bar present in encounter, order, dispense, approval and call-centre screens; search and worklists open the profile; module deep-links are permission-gated.
- [ ] Print/export summary generated server-side from the same projection, watermarked and audited.
- [ ] Every open writes one `ProfileViewed` audit event listing served + withheld sections.
- [ ] p95 < 2.5s full profile, < 400ms context bar; bilingual AR/EN, WCAG 2.2 AA, axe clean.
