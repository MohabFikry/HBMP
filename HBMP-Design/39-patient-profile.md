# 39 — Unified Patient Profile (role-projected 360)

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [11-permission-matrix.md](11-permission-matrix.md) · [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md) · [38-policy-member-administration.md](38-policy-member-administration.md) · [19-audit-strategy.md](19-audit-strategy.md) · [0B-DESIGN-SYSTEM-UI.md](0B-DESIGN-SYSTEM-UI.md)
> Build prompt: [claude-code-prompts/phase-20-patient-profile.md](claude-code-prompts/phase-20-patient-profile.md)

**One patient record, many lenses.** A single canonical profile — identity and photo, coverage and eligibility, alerts and allergies, past medical history, encounters, investigations and results, prescriptions and dispensing, authorizations, referrals, documents, notes, financial, case, timeline and **call history** — reachable from search and from every worklist, and **projected server-side to exactly what the viewing role may see**.

---

## 1. Why this needs care

This is the **highest-risk feature in the platform**. Every other module is naturally scoped: a lab sees its queue, a pharmacy sees its prescriptions, finance sees amounts. The patient profile deliberately aggregates all of it into one screen. Built naively — one fat payload filtered in the browser — it becomes the single bypass that undoes reception ≠ EMR, finance ≠ diagnoses, labs ≠ prescriptions, pharmacy ≠ results and the phase-14 sensitive-result gate simultaneously.

Two rules make that impossible rather than merely discouraged:

1. **Composition happens server-side, under the caller's own token — never a service account.** A privileged aggregator that fetches everything and then filters is the classic aggregation vulnerability. Each owning service must still apply its own authorization to the call; the profile layer adds section shaping on top. Two independent layers, neither sufficient alone.
2. **The wire payload contains only what the role may see.** Not hidden with CSS, not `display:none`, not present-but-unrendered. If a section is withheld, the field is absent from the JSON.

## 2. Consolidation first (this replaces, it does not add)

Four partial 360s already exist and are diverging:

| Existing | Where | Fate |
|---|---|---|
| Case beneficiary-360 (assignment-scoped) | `case-service` | Becomes the `case` **section** of the profile |
| Call-centre member 360 (verification-gated) | `callcentre-service` | Becomes the call-centre **projection** of the profile |
| Call interaction log ([phase 15](claude-code-prompts/phase-15-call-centre-portal.md)) | `callcentre-service` | Becomes the `callHistory` **section** — same rows, role-projected |
| Administrative 360 ([38 §4.6](38-policy-member-administration.md)) | policy/patient | Becomes the administrative **sections** |
| EMR clinical context | `emr-service` | Becomes the clinical **sections** |

A fifth overlapping aggregate would guarantee drift. Phase 20 defines **one contract**; the four above are re-pointed at it and their bespoke shapes retired.

## 3. Sections (the unit of access)

The profile is a set of independently-gated **sections**. Each returns one of three states: **Visible** (content), **Restricted** (exists, content withheld, with a reason and — where applicable — a request-access action), or **NotApplicable** (nothing to show).

| # | Section | Contents |
|---|---|---|
| 1 | **Header / Identity** | Name (AR/EN), member no, age/sex, **photo**, member status, branch, preferred language, contact summary |
| 2 | **Alerts** | Allergies, critical flags, drug-interaction warnings, no-show/eligibility flags — always first, always prominent |
| 3 | **Coverage & Eligibility** | Payer, policy, plan (label + version), effective dates, per-category limits with consumed/remaining, **per-tier cost share** ([38](38-policy-member-administration.md)), waiting-period state |
| 4 | **Past Medical History** | Structured conditions + narrative history + uploaded historical records ([38 §5b](38-policy-member-administration.md)) |
| 5 | **Encounters / Visits** | Chronological visits, branch, clinician + specialty, reason, status |
| 6 | **Investigations & Results** | Orders, fulfillment status, results — **sensitivity-gated per [37 §6](37-branch-scoping-and-clinical-sensitivity.md)** |
| 7 | **Prescriptions & Dispensing** | Rx lines, dispensing status, batch/expiry, substitutions |
| 8 | **Authorizations** | Requests, decisions, rationale, validity |
| 9 | **Referrals** | Open and closed referrals with loop status |
| 10 | **Documents** | Classified documents ([38 §5b](38-policy-member-administration.md)) — metadata always, download separately gated |
| 11 | **Notes** | Policy/member notes ([38 §5](38-policy-member-administration.md)), class-projected |
| 12 | **Financial** | Claims, cost-share owed, settlement status — **never diagnoses** |
| 13 | **Case management** | Assigned cases, coordination tasks, escalations |
| 14 | **Timeline** | The unified change/access history ([38 §5c](38-policy-member-administration.md)) |
| 15 | **Call history** | Every contact-centre interaction: **direction (inbound/outbound)**, date/time, duration, agent, branch, reason code, outcome, verification result, **a per-call summary**, and links to what the call produced (appointment, contact change, complaint). Copyable — see [§5b](#5b-call-history-section-15) |

## 4. Role → section matrix

`V` = visible · `R` = restricted (existence only) · `—` = not returned at all

| Role | 1 Hdr | 2 Alert | 3 Cov | 4 PMH | 5 Enc | 6 Inv | 7 Rx | 8 Auth | 9 Ref | 10 Doc | 11 Note | 12 Fin | 13 Case | 14 Time | 15 Calls |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Reception** | V | V | V | — | V(meta) | — | — | V(status) | V | R | V(admin) | — | — | V(admin) | V(operational) |
| **Call Centre** | V | V | V | — | V(meta) | — | — | V(status) | V | R | V(admin) | — | — | V(admin) | V (full) |
| **Doctor/Nurse** (treating) | V | V | V | V | V | V | V | V | V | V | V | — | V | V | V(operational) |
| **Doctor** (non-treating) | V | V | R | R | R | R | R | R | R | R | R | — | — | R | R |
| **Lab / Imaging** | V(min) | V(allergy) | — | — | — | V(own orders) | — | — | — | — | — | — | — | — | — |
| **Pharmacy** | V(min) | V(allergy) | V(pharmacy limit) | — | — | — | V(own Rx) | — | — | — | — | — | — | — | — |
| **Medical Approval** | V | V | V | V | V | V* | V | V | V | V | V | — | V | V | V(operational) |
| **Medical Director** | V | V | V | V | V | V* | V | V | V | V | V | V(summary) | V | V | V (full) |
| **Case Manager** (assigned) | V | V | V | V(summary) | V | R | R | V | V | V(admin) | V | — | V | V | V (full) |
| **Finance / Claims** | V(min) | — | V(amounts) | — | V(meta) | — | — | V(cost) | — | R | V(fin) | V | — | V(fin) | V(meta) |
| **Beneficiary Mgmt** | V | V | V | R | V(meta) | — | — | V(status) | V | V(admin) | V | — | — | V(admin) | V (full) |
| **Org/Super Admin** | V(min) | — | — | — | — | — | — | — | — | — | — | — | — | V(access) | — |

\* Sensitive results remain **existence-only even for the approval team** until a [37 §6](37-branch-scoping-and-clinical-sensitivity.md) grant exists — the profile does not weaken that gate.

Cross-cutting: **treating relationship** governs clinical sections for clinicians; **provider ownership** limits labs/pharmacies to their own items; **branch scope** ([37 §3](37-branch-scoping-and-clinical-sensitivity.md)) narrows operational sections; **call-centre verification** ([phase 15](claude-code-prompts/phase-15-call-centre-portal.md)) must pass before any section but the identity challenge; **payer scope** applies to coverage/financial.

## 5. Photo — treat as sensitive, not decorative

A beneficiary photo materially aids identification at reception and prevents card-sharing, but for a refugee population it is **identity-sensitive biometric-adjacent data**:

- Captured with **explicit recorded consent** (consent document per [38 §5b](38-policy-member-administration.md)); refusal is permitted and must not block care.
- Stored in `document-service` with class `IdentityPhoto`, never in a public bucket; served as a **short-TTL signed thumbnail**, never a permanent URL.
- Visible only to roles with a legitimate identification need (reception, call centre, clinicians, beneficiary management). **Finance, labs, pharmacies and admin do not receive it.**
- Every retrieval audited; never included in exports, extracts, notifications, or the FHIR façade without an explicit separate decision.
- Replaceable with versioning; old versions retained per retention policy, never silently overwritten.

## 5b. Call history (section 15)

Every contact with the beneficiary that happened by phone. The rows already exist — `call_interaction` in `callcentre-service` ([phase 15](claude-code-prompts/phase-15-call-centre-portal.md)) — this section **surfaces them to other roles under projection**; it does not create a second log.

### What a row shows

| Field | Notes |
|---|---|
| **Direction** | `Inbound` / `Outbound` — the beneficiary called us, or we called them. Rendered with **four cues** (hue + arrow icon + shape + the word), never colour alone |
| Date/time, duration | `started_at` → `ended_at`, Africa/Cairo display |
| Agent + branch | Who handled it, under which branch scope |
| Reason code | The phase-15 enum (BookAppointment, EligibilityEnquiry, Complaint, …) |
| Outcome | Resolved / FollowUpRequired / Transferred / Abandoned / NoAction |
| Verification | Passed / Failed / Not attempted — **which identifier *types*** were confirmed, never the values |
| **Summary** | A short, structured account of *what the call was about and what was done* |
| Linked artefacts | Appointment booked/moved/cancelled, contact change, complaint ref, follow-up task |

### Summary is a first-class field, not a free-text dump

Add `summary varchar(500)` to `call_interaction`, **separate from the existing `notes`**. This separation is the point:

- **`summary`** is the operational account intended to be read by other roles later. It is written by the agent at wrap-up, required when the interaction closes with an outcome other than `Abandoned`, and length-capped so it stays a summary.
- **`notes`** stays the agent's fuller working text and is **not** promoted to other roles by default.

Splitting them means widening the audience for call history does not silently widen the audience for whatever an agent typed mid-call. Both are versioned — corrections are edits with history and a visible "edited" marker, never silent overwrites ([phase 15](claude-code-prompts/phase-15-call-centre-portal.md) already requires this for interactions).

**Clinical content does not belong in a call summary.** Agents are not clinicians; a summary saying "complained of chest pain" creates an unreviewed clinical record in an operational store. The UI states this at the point of writing, and any clinical detail an agent must pass on goes through the existing escalation/case path, not the summary line.

### Projection levels

| Level | Sees |
|---|---|
| **Full** | Every field above, including the summary and the verification detail |
| **Operational** | Direction, date/time, duration, reason code, outcome, summary, linked artefacts — **no verification detail, no agent notes** |
| **Meta** | Direction, date/time, reason code, outcome only — **no summary text** |

Finance/claims get **Meta**: enough to see that a complaint or billing call occurred, without the narrative. A non-treating doctor gets **Restricted** — existence only.

### Copy summary

Each row carries a **copy icon** that puts a clean plaintext block on the clipboard, so an agent, coordinator or approver can paste it into a case note, a handover, a ticket or an email without retyping or screenshotting.

```
[Outbound] 2026-07-24 14:32 (6m 12s) · Nasr City · Agent: R. Adel
Member: MRS-M-014882 · Ref: CALL-2026-004137
Reason: RescheduleAppointment · Outcome: Resolved
Appointment APT-2026-8841 moved from 25 Jul to 30 Jul at the member's request;
member confirmed the new slot on the call.
```

Four rules make this safe rather than a leak:

1. **The clipboard text is generated server-side from the same projection** and returned as a `copyText` field on the row. The client copies a string it was given; it never assembles one from data it holds. A Meta-level viewer's `copyText` therefore has no summary line in it — there is nothing to strip client-side, because it was never sent.
2. **Copying is an audited PHI action** (`CallSummaryCopied`, with the interaction ref) — moving PHI to the clipboard is the moment it leaves the platform's control, and that is exactly the moment worth logging. Copy-all-visible is available; it emits one event listing the refs.
3. **The block always carries provenance** — member ref, call ref, direction, timestamp — so a pasted summary can be traced back and cannot be mistaken for a clinical note.
4. **No verification detail and no agent notes are ever in `copyText`**, at any level.

Accessibility: the copy control is a real `<button>` with an accessible name naming the call (`Copy summary of outbound call on 24 July`), keyboard reachable, ≥44px, with an `aria-live` confirmation — not a hover-only affordance and not an icon with no name.

## 6. Behaviour

**Entry points.** Global search (permission-scoped), every worklist row, the call-centre workspace after verification, and a deep link `/patients/{beneficiaryId}` that resolves to the caller's projection. Unauthorized deep links return 403 + audit, never a blank page.

**Patient context bar.** A compact identity strip (name, member no, age/sex, photo, status, alerts) that follows the user into encounter, order, dispense and approval screens, so the record on screen is never ambiguous.

**Composition & degradation.** Sections are fetched in parallel with per-call timeouts. A failing section renders "temporarily unavailable" with retry — it must **never** be indistinguishable from "no data" or "not permitted". Three states, three distinct treatments.

**Audit.** Opening a profile is a PHI read: one audit event recording actor, beneficiary, **which sections were actually served**, purpose, and correlation id. Restricted sections are logged as withheld. This is what makes "who looked at this patient" answerable.

**Export / print summary.** A role-projected printable summary, generated server-side from the same projection, watermarked with viewer + timestamp, and **audited as a PHI export**. It can never contain a section the viewer could not see on screen.

## 7. Invariants

1. **Server-side projection only** — the payload never contains a withheld field.
2. **Compose under the caller's token; never a privileged service account.**
3. **The profile weakens no existing gate** — treating relationship, provider ownership, branch scope, payer scope, call-centre verification and sensitive-result grants all still apply; the profile is strictly an intersection, never a union.
4. **Owns no data** — it composes from owning services; a new copy of clinical data here would be a second source of truth.
5. **Every open is audited with the served section list**; every restricted section is visible as *restricted*, so users request access rather than assume absence.
6. **Break-glass works but is loud** — reuse the existing machinery; never a silent elevation.
7. **Copy-to-clipboard text is server-generated from the served projection** — the client never assembles it. Copying is audited; identifier values, verification detail and agent notes are never in it.

## 8. Acceptance criteria

- [ ] One canonical profile contract with **15** independently-gated sections; the four existing partial 360s re-pointed onto it and their bespoke shapes retired.
- [ ] Composition is server-side under the caller's token; an architecture test forbids a service-account fetch path in the profile layer.
- [ ] Role × section matrix enforced and proven by reflection tests over the **serialized payload** for every role: reception/call-centre receive no clinical fields; finance receives no diagnosis and no photo; lab sees only its own orders; pharmacy only its own Rx; a non-treating doctor gets existence-only.
- [ ] Sensitive results stay existence-only **including for the approval team** until a [37 §6](37-branch-scoping-and-clinical-sensitivity.md) grant exists.
- [ ] Photo consent-gated, signed short-TTL, role-limited, audited, excluded from exports.
- [ ] Call history shows **inbound/outbound with four cues**, a per-call summary, and projects at Full / Operational / Meta per role; finance receives no summary text; a non-treating doctor gets existence only.
- [ ] `summary` is a distinct capped field from agent `notes`; notes are not promoted to other roles; edits keep history.
- [ ] Copy-summary control returns **server-generated `copyText`**, carries provenance (member + call ref + direction + timestamp), never contains verification detail or notes, is keyboard-accessible with a descriptive name, and writes a `CallSummaryCopied` audit event.
- [ ] Restricted / unavailable / empty are three visually and semantically distinct states.
- [ ] Every profile open writes one audit event naming the sections served; exports audited separately.
- [ ] Patient context bar present in encounter, order, dispense and approval screens.
- [ ] Bilingual AR/EN with full RTL, WCAG 2.2 AA, keyboard navigable, axe clean.

---

### Cross-references
Permissions: [11-permission-matrix.md](11-permission-matrix.md) · Sensitivity/branch: [37](37-branch-scoping-and-clinical-sensitivity.md) · Coverage/notes/documents/timeline: [38](38-policy-member-administration.md) · Call interactions & verification: [claude-code-prompts/phase-15-call-centre-portal.md](claude-code-prompts/phase-15-call-centre-portal.md) · Audit: [19-audit-strategy.md](19-audit-strategy.md) · UI: [0B](0B-DESIGN-SYSTEM-UI.md) · Build: [claude-code-prompts/phase-20-patient-profile.md](claude-code-prompts/phase-20-patient-profile.md)
