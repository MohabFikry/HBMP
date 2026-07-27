# ADR-0026 — Patient profile: server-side role projection, composed under the caller's token, owning no data

- **Status:** Accepted
- **Date:** 2026-07-28
- **Phase:** 20 (design [`39-patient-profile.md`](../../HBMP-Design/39-patient-profile.md))
- **Supersedes:** nothing. **Consolidates:** the four partial 360s listed in §2 below.

> Numbering note: the build prompt calls for "ADR-0023". That number was taken by
> [`0023-utilization-read-model.md`](0023-utilization-read-model.md) in phase 19.6, so this decision is recorded
> as 0026. Renumbering a merged ADR would break every inbound reference to it.

## Context

The unified patient profile aggregates everything about one person onto one screen: identity and photo, alerts,
coverage, past medical history, encounters, investigations and results, prescriptions, authorizations,
referrals, documents, notes, financial, case, timeline and call history.

Every other module in the platform is naturally scoped. A lab sees its queue, a pharmacy sees its
prescriptions, finance sees amounts. This feature deliberately removes that scoping and puts the whole record in
one place, which makes it the single point where **reception ≠ EMR**, **finance ≠ diagnoses**, **labs ≠
prescriptions**, **pharmacy ≠ results** and the phase-14 sensitive-result gate could all be undone at once.

Four partial 360s already existed and were diverging: the case beneficiary-360, the call-centre member 360, the
phase-19 administrative 360, and the EMR clinical context. A fifth aggregate would have guaranteed drift.

## Decision

### 1. One contract, fifteen independently-gated sections

`GET /api/v1/patients/{beneficiaryId}/profile` returns `{ beneficiaryId, servedAt, sections[] }`, where each
section is `{ key, state, reasonCode?, requestAccessAction?, data? }` and `state` is one of `Visible`,
`Restricted`, `NotApplicable`, `Unavailable`.

The four existing 360s are re-pointed onto this contract rather than kept alongside it.

### 2. Projection happens server-side; a withheld field is absent from the JSON

The role × section matrix (design 39 §4) is expressed as **data** in `libs/authz/ProfilePolicies.cs` — one cell
per (role, section), each naming the projection variant and the ABAC conditions it depends on. The composer
consults it **before** fetching, so a section a role can never see is never requested from its owning service at
all.

The wire payload is serialized with `JsonIgnoreCondition.WhenWritingNull`, and every variant projection returns
a **narrower record** rather than blanking a field. Proven by reflection tests that read the *serialized JSON*
with marker strings: if a diagnosis appears anywhere in reception's payload, under any key, in any shape, the
build fails.

### 3. Composition runs under the caller's own token — never a service account

Each `ISectionProvider` calls its owning service over HTTP forwarding the caller's bearer (and
`X-Active-Branch`). That service applies its own authorization exactly as it would to a direct request, so the
profile is **two independent layers**, neither sufficient alone: the owning service's gate, and the section
matrix on top.

There is no client-credentials path in `profile-service`, and an architecture test fails the build if one
appears. `ProfileComposer` also refuses at runtime when the caller's bearer is absent, rather than falling back
to anything.

### 4. The profile owns no data

`profile-service` has no `DbContext`, no migrations and no schema. It appears on the RLS exemption register in
`libs/architecture` with that reason recorded, because there is no connection on which to bind a tenant GUC.

### 5. Restricted, Unavailable and NotApplicable stay three distinct states

A per-section timeout degrades **that** section; the rest of the profile still renders. A failing upstream is
never reported as empty.

### 6. Call history is projected upstream, and the clipboard text is derived from what was served

`call_interaction.summary` is a new, capped column **separate from the existing `notes`**. callcentre-service
projects rows to Full / Operational / Meta and generates each row's `copyText` **from the narrowed row**, in the
same code path. A client-supplied level may narrow, never widen.

### 7. The photo is a separate resource with a narrower allow-list

`IdentityPhoto` is a `DocumentClass` in policy-service, consent-gated on upload, resolved to a short-TTL signed
URL, and excluded from the general document list. `profile-service` 302s to that URL and audits every retrieval;
the bytes never pass through the composition layer.

## Alternatives considered

### Rejected: one fat payload, filtered in the browser

The obvious implementation. One endpoint returns everything about the patient; the SPA renders the parts the
user's role should see.

**Why it was rejected.** It is the classic aggregation vulnerability, and it fails in four separate ways at
once:

1. **The data has already left.** Anyone with dev-tools, a proxy, or the network tab reads the whole record.
   `display: none` is a styling instruction, not an access control, and neither is "the component doesn't render
   that field".
2. **It centralises the decision in the least trustworthy place.** The browser is code the organisation does not
   control at runtime. A rule enforced there is a rule enforced by whoever is holding the laptop.
3. **It makes every future client a new breach.** The mobile app, the print view, an integration, a CSV export —
   each one re-implements the filter, and the one that forgets is the one nobody notices, because the payload
   looks the same as it always did.
4. **It cannot express the gates that actually apply.** Treating relationship, provider ownership, branch scope,
   payer scope, call-centre verification and sensitive-result grants are all *server* facts. A client filter
   would either have to be told them — which discloses them — or approximate them, which is worse.

The cost of the chosen design is real: fifteen providers, a fan-out, per-section timeouts, and a projection
dispatcher that has to be kept exhaustive. That is the price of the payload being correct rather than the
rendering being correct.

### Rejected: a privileged aggregator that fetches everything, then filters

A middle position — server-side filtering, but the composition runs as `profile-service` with a service account
so no downstream call can fail on authorization.

**Why it was rejected.** It removes the second layer entirely. The owning services stop being a check and become
a data source, and the whole guarantee rests on the aggregator's filter being right. Worse, the failure is
invisible: a service account makes every fetch succeed, so the resulting profile is *complete* rather than
*correct*, and looks healthier than the correct one. This is why the prohibition is enforced by an architecture
test rather than a code-review convention — it is the kind of change that gets added to fix a 401 in staging.

### Rejected: a fifth aggregate alongside the existing four

Cheapest to build, and it was the status quo trajectory. Rejected because four aggregates were already
diverging; a fifth would not have replaced any of them, and the profile would have become the place where the
other four disagreed.

### Rejected: caching the composed profile by beneficiary id

Tempting for the p95 budget. Rejected outright: the composition depends on role, treating relationship, branch,
payer scope and live grants, and a cache keyed on fewer dimensions than the decision depends on is a breach, not
a bug — the phase-18 X9 lesson. Where memoization exists, it is **per request** (one call to policy's
administrative-360 shared by the four sections derived from it), so its lifetime *is* its key.

### Considered and adopted: the call-centre endpoint stays a thin facade

Build prompt 20.2 asked for a choice between (a) keeping the callcentre endpoint as a facade that enforces
verification then delegates, and (b) having the SPA call the profile directly with verification context.

**Chosen: both halves of (a), plus a hard check in the profile.** The callcentre 360 endpoint delegates, and
`profile-service` independently refuses any call-centre principal that cannot name a verified interaction —
confirmed against callcentre-service, which remains the only place the verification rule lives. The facade is
convenience; the check is the control. Relying on the facade alone would mean the profile's own front door was
open to a call-centre token that skipped it.

## Consequences

- **Positive.** One place to reason about who sees what about a patient. The matrix is readable against the
  design doc line by line and swept by a table-driven test over every role. Adding a section is a cell in a
  table plus a provider, and forgetting the projector is a build failure.
- **Positive.** Every profile open writes one `ProfileViewed` audit event naming served *and* withheld sections,
  which is what makes "who looked at this patient, and what did they see" answerable.
- **Negative.** The fan-out touches ~8 services. p95 budgets (2.5s full profile, 400ms context bar) are met by
  parallelism and per-call timeouts, not by caching, so a slow upstream shows up as a degraded section rather
  than a slow page — visible, but visibly worse than a cached lie.
- **Negative.** Section providers parse upstream JSON rather than sharing typed DTOs, deliberately: a
  compile-time coupling between fifteen services and their aggregator would make the composition layer a
  deployment bottleneck. The cost is that an upstream field rename fails at runtime as `Unavailable` rather than
  at build time. Contract tests are the mitigation.
- **Operational.** `profile-service` is stateless and horizontally scalable; it needs no database and no
  migration in any environment.

## References

Design [39](../../HBMP-Design/39-patient-profile.md) §1–§7 · [11-permission-matrix](../../HBMP-Design/11-permission-matrix.md) ·
[37 §3/§6](../../HBMP-Design/37-branch-scoping-and-clinical-sensitivity.md) ·
[38 §5/§5b/§5c](../../HBMP-Design/38-policy-member-administration.md) ·
[19-audit-strategy](../../HBMP-Design/19-audit-strategy.md) · build prompt
[phase-20](../../HBMP-Design/claude-code-prompts/phase-20-patient-profile.md)
