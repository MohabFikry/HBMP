# ADR-0035 — The approval supervisor's authority: master lists, the approvals engine, and document validity

- **Status:** Proposed
- **Date:** 2026-08-05
- **Phase:** 27
- **Supersedes / amends:** extends the principle stated in `libs/authz/AdminPolicies.cs` (`EditValidityPolicy`)
- **Related:** ADR-0034 (fulfilment authorization), `18-security-model.md`, `23-state-machines.md`, `19-audit-strategy.md`

---

## 1. The principle this rests on

The codebase already states it, in the comment on `AdminPolicies.EditValidityPolicy`:

> A SEPARATE action from `EditConfig`, and deliberately held by clinical governance rather than by the platform
> admins. How long a prescription remains safe to dispense is a clinical judgement about how fast a patient's
> condition moves, not a system setting — **the Medical Director who supervises the approval queue is the
> person who lives with the consequence of getting it wrong, and is the one the extension requests land on
> when it is too short.**

That is the whole argument, and it generalises. Every parameter in this ADR has the same shape: **the
supervisor sets it, and the supervisor absorbs the consequence of setting it wrong.**

| Parameter | Set too tight → | Set too loose → |
|---|---|---|
| Validity period | extension requests flood their queue | unsafe dispensing they answer for |
| Pre-auth trigger | every order needs a decision — their queue | spend leaves without review |
| Auto-decision | nothing is automated; queue grows | payments made with no human in the loop |
| Routing / SLA | work lands on the wrong desk | breaches they are measured on |
| Master list entry | a wrong code misroutes a diagnosis into their queue | a wrong price is paid |
| Document validity | a lapsed card blocks a refugee at reception | an expired credential goes unnoticed |

None of these is a platform setting. Each is a clinical or benefit judgement whose failure mode arrives on one
desk. **They belong to the person holding that desk.**

---

## 2. Context: what is already true

Findings from the current tree, not assumptions.

### 2.1 The authority is already granted, and is unreachable

`medical_director` **already holds** `admin:edit-masterdata` and `admin:edit-validity-policy` in
`libs/authz/AdminPolicies.cs`, and its role carries `admin:read` + `admin:write`. The server endpoint exists
and is complete: `POST /api/v1/admin/master-data` — effective-dated, versioned, rationale-mandatory, audited,
gated on `AdminPolicies.EditMasterData`.

Two things stop it being usable:

1. **`portalForRole` returns exactly one portal per role.** The Master Data screen lives in the `admin`
   portal (`org_admin`). A director has the `director` portal and no route to `/admin/master-data`.
2. **The screen is read-only.** `AdminMasterData` lists the versions in force and offers no editor, while the
   write endpoint sits unused.

So this is not "grant the supervisor authority" — the authority was granted and then had no door.

### 2.2 Master data cannot be written directly, and must not be

`MasterDataAuthzTests.The_reference_catalogue_exposes_no_write_path` asserts that masterdata-service exposes
no POST/PUT/PATCH/DELETE outside a narrow allow-list of read-shaped POSTs. Its own comment gives the reason:

> Codes are safety-critical — a wrong ICD mapping misroutes a diagnosis, a wrong ATC entry breaks interaction
> checking — so changes go through admin-service's effective-dated, versioned, audited governance path.

**This ADR does not weaken that test.** Every master-list edit continues to go through admin-service.

### 2.3 There is no approvals engine

`RequiresPreauthAsync` reads `RequiresPreauth` off the plan version's cost-share terms. That is the entire
pre-auth decision. There is no rule table, no authoring surface, no auto-decision path, and no routing rule —
assignment is manual (`POST /{id}/assign`). SLA exists only as a reported TAT (`/tat-summary`), not as a
configured target.

### 2.4 Document expiry: the dates exist, the policy does not

- `provider.provider_credential` has `valid_from` / `valid_to`.
- `provider.practitioner` has `license_expiry`, with a working sweeper.
- **The warning thresholds are hard-coded**: `PractitionerLicence.WarningDays = [90, 60, 30]`.
- `document.document` has `doc_type` but **no expiry column at all**.
- `patient.beneficiary_identifier` has no expiry — a refugee card's expiry is not modelled.

So: some documents expire and nobody may configure how; others cannot expire at all.

---

## 3. Decision 1 — The supervisor portal gains a governance group

Add a `Governance` section group to the `director` portal:

| Section | Path | Screen | Gate |
|---|---|---|---|
| Master Lists | `/director/master-lists` | `MasterListAdmin` (new) | `admin:edit-masterdata` |
| Approvals Engine | `/director/engine` | `ApprovalEngineAdmin` (new) | `auth:configure` (new) |
| Validity Periods | `/director/validity` | `ValidityPolicyAdmin` (exists) | `admin:edit-validity-policy` |
| Document Validity | `/director/document-validity` | `DocumentValidityAdmin` (new) | `admin:edit-validity-policy` |

**Route duplication, not multi-portal.** `ALL_ROUTES` is derived from the portal definitions and
`registry.tsx` maps fully-qualified paths, so a section may point at a screen another portal also renders.
Making `portalForRole` return many portals would change navigation, the command palette, deep-link resolution
and the portal-scoped `spa-scopes` gate — a structural change to serve one screen. The screens are already
permission-gated server-side; a second door to the same room is the smaller and safer change.

**`admin` keeps its Master Data section.** `org_admin` still needs the read view. The director gets the
*editor*; nothing is taken away.

---

## 4. Decision 2 — Master-list editing, through the governance path

`MasterListAdmin` is an editor over the **existing** `POST /api/v1/admin/master-data`. No new service, no new
table, no write path in masterdata-service.

**What an edit is.** Appending a new effective-dated version. Nothing is updated in place and nothing is
deleted — `Retired` is a flag on a new version, so a historical record still resolves the code it was written
against via `/master-data/{system}/{code}/as-of`.

**Mandatory rationale.** Already enforced server-side (`rationale-required`). The UI must present it as a
first-class field, not a footnote: the rationale is what an auditor reads in three years when asking why an
ATC entry changed the week a claim was denied.

**A diff before commit.** The editor shows the version in force beside the proposed one, attribute by
attribute. A code table edited blind is how a wrong mapping ships.

**Scope of the vocabulary.** The `system` is a closed enum (`CodeSystem`). The editor offers only those
values — a free-text system field would let a supervisor create a code system nothing reads.

> **Open question for review.** Should the director be able to edit *every* code system, or only the
> clinically-owned ones (ICD, ATC, LOINC, CPT) with the administrative ones (branch codes, payer codes) left
> to `org_admin`? My recommendation: **restrict to the clinical systems**, on the same principle as §1 — a
> director does not absorb the consequence of a wrong branch code. This would need a new ABAC condition.

---

## 5. Decision 3 — The approvals engine

Three rule families, each effective-dated, each authored by the supervisor, each **fail-closed**.

### 5.1 Shape common to all three

A rule lives in approvals-service, in a new `approvals.rule` table:

```
rule_id, tenant_id, family, priority, predicate_json, action_json,
effective_from, effective_to, version_no, authored_by, rationale, enabled
```

- **Effective-dated, append-only** — the same governance shape as master data. A decision made last Tuesday
  must be explainable against the rules in force last Tuesday, not today's.
- **`rationale` mandatory**, for the same reason as §4.
- **Deterministic ordering** by `priority`, then `rule_id`. Two rules that both match must not race.
- **Fail-closed.** If the rule set cannot be read, the request goes to a human. An engine that cannot reach
  its rules must never conclude "no rule matched, therefore approve".
- **RLS by `tenant_id`**, like every other table.

### 5.2 Pre-auth trigger rules

Today: one boolean per plan-version × category × tier. Proposed: that stays the **floor**, and rules may only
*add* a pre-auth requirement, never remove one.

> **Why one-directional.** The plan's `RequiresPreauth` is a contractual term between the payer and Mersal. A
> local rule that switched it off would silently override a contract, and the divergence would surface as a
> denied claim months later. Rules may say "also require approval when …"; they may not say "stop requiring".

Predicates over: benefit category, service code, estimated amount, provider tier, beneficiary programme,
cumulative spend in a window.

### 5.3 Auto-decision rules — and a recommendation against half of it

**Auto-approve: yes, bounded.** A rule may approve without a human when every condition holds:
- an explicit amount ceiling, per rule, below a tenant-wide hard maximum;
- the category is not excluded by the plan;
- no clinical warning is outstanding on the request;
- the rule is enabled and in force.

The resulting authorization records `decided_by = rule:<rule_id>@v<version_no>` — never a person's subject.
Attributing a machine decision to a human is a falsified audit record, and this platform's audit is
hash-chained precisely so that it cannot be.

**A kill switch.** One tenant-level toggle disables all auto-decision immediately, without editing rules.
When it is off, every request queues for a human. This is the control you reach for at 02:00 when a rule is
misbehaving, and it must not require authoring anything.

**Auto-reject: I recommend NOT building it.**

The two failure modes are not symmetric. An auto-approval that was wrong costs the payer money, and a human
reviews the claim later. An auto-rejection that was wrong **denies care to a refugee**, with no human having
looked, and — per the reasoning already written into `libs/benefit-pricing` — "a refugee at a counter has no
reviewer in the loop and no recovery path". The throughput benefit is real but it is available without the
harm: a rule that would have rejected instead **routes to a named queue with high priority and a stated
reason**. The reviewer sees "the engine believes this is excluded under §4.2 — confirm", and the decision
still has a person's name on it.

> This is a recommendation, not a refusal. If you want auto-reject, say so and I will build it with an
> appeal path and a mandatory daily review of every auto-rejection. But I think the queue-with-a-reason
> version gets you nearly all the throughput at none of the risk, and I would rather say so now.

### 5.4 Routing and SLA rules

- **Routing**: predicate → target queue / reviewer group. Changes *who* decides, never *what* is decided, so
  it carries the least risk of the three and can ship first.
- **SLA**: predicate → target hours, with the existing `zSla` shape and the escalation the worklist already
  renders. Today the TAT report measures against nothing configurable.

**Routing must never strand work.** A request matching no routing rule goes to the default queue. A rule set
that can route into a queue nobody watches is worse than no routing at all, so the editor shows the current
watcher count per queue and refuses to save a rule targeting an unwatched one.

---

## 6. Decision 4 — Document validity policy

Mirror `libs/validity` exactly. It is a solved, tested pattern in this codebase, and a second shape for the
same idea would be the drift this ADR exists to prevent.

**A single generic policy keyed by document type**, per the review decision — not per-family special cases.

```csharp
public enum DocumentKind
{
    // Beneficiary identity — the ones whose lapse blocks eligibility.
    RefugeeCard, NationalId, Passport, ResidencyPermit,
    // Provider credentials.
    PractitionerLicence, FacilityAccreditation, ProviderContract,
}
```

Stored as `system_config` keys — `document-validity.refugee-card.days` — read through a
`DocumentValidityPolicy` class with `DefaultDays`, `MinDays`, `MaxDays`, exactly as `ValidityPolicy` does.

**Two numbers per kind, not one:**

| | What it sets | Why the supervisor owns it |
|---|---|---|
| `days` | how long the document is valid from issue | a renewal cadence, not a system setting |
| `warnDays` | how far ahead the warning fires | today `[90, 60, 30]` is a hard-coded constant in `PractitionerLicence` |

**What this changes in the schema (expand/contract):**

1. **Expand** — add `expires_on date NULL` to `document.document`; add `expires_on` to
   `patient.beneficiary_identifier`. Nullable, so nothing existing breaks.
2. Backfill where a date is derivable; leave NULL where it is not.
3. **Contract** — later, and only after the sweeper has run clean, consider `NOT NULL` for the kinds that
   must always carry one.

**A lapsed identity document must WARN, not BLOCK.** This is the standing platform rule — benefit rules may
block; anything clinical or administrative may only warn with an acknowledgement and a reason. A refugee
whose card expired last week is still a person in front of a receptionist, and a hard block would turn a
paperwork lapse into a refusal of care. The eligibility check surfaces it; the receptionist acknowledges it;
the case lands on the supervisor's desk. Which is, again, §1.

**`NULL` is never rendered as "valid".** A document with no expiry recorded is *unknown*, and the screens say
so — the same rule as "check unavailable is never rendered as OK".

---

## 7. New authorization

| Action | Roles | Scopes | Notes |
|---|---|---|---|
| `auth:configure` | `medical_director`, `super_admin` | `auth:configure` (new scope) | Author engine rules. Separate from `auth:decide`: authoring the rule that decides a thousand cases is a different power from deciding one. |
| `admin:edit-masterdata` | *(unchanged)* | *(unchanged)* | Already held by `medical_director`. |
| `admin:edit-validity-policy` | *(unchanged)* | *(unchanged)* | Extended in meaning to cover document validity. |

`auth:configure` is deliberately **not** granted to `medical_approval`. A reviewer who could edit the rule
that routes their own work could route it away from themselves.

---

## 8. Consequences

**Good**
- The authority that already exists becomes reachable, and the built-and-unused write endpoint gets a caller.
- Every parameter that generates approval workload becomes owned by the desk that absorbs it.
- Hard-coded constants (`WarningDays`) become governed, effective-dated, audited configuration.

**Costs and risks**
- **The engine is a new decision-maker in a benefit path.** Mitigated by: fail-closed, effective-dated rules,
  rule-attributed audit, a kill switch, and no auto-reject.
- **A second door to master-data editing** widens who can change safety-critical codes from one role to two.
  Mitigated by the unchanged governance path — versioned, rationale-mandatory, fully audited — and by the
  open question in §4 about restricting to clinical code systems.
- **Two schema expansions** on `document` and `patient`. Both nullable-add, backward compatible.

**Explicitly out of scope**
- Substitution / equivalence lists (deselected at review). ADR-0034's arrangement stands: examination
  substitutions ask the approval team, because no equivalence set exists.
- Any write path in masterdata-service.
- Auto-reject (see §5.3).

---

## 9. How this will be proved

| Claim | Test |
|---|---|
| Master data still has no write path | `MasterDataAuthzTests.The_reference_catalogue_exposes_no_write_path` — must stay green untouched |
| A rule edit is effective-dated and append-only | new: a second edit leaves the first version resolvable `as-of` its date |
| The engine fails closed | new: rule store unavailable → request queues for a human, never auto-approves |
| An auto-approval is attributed to the rule | new: `decided_by` matches `rule:<id>@v<n>`, never a subject |
| The kill switch stops auto-decision without editing rules | new |
| A reviewer cannot author rules | new authz test: `medical_approval` → 403 on `auth:configure` |
| Routing never strands a request | new: a request matching no rule reaches the default queue |
| An expired identity document warns and does not block | new: eligibility returns a warning, not a refusal |
| Unknown expiry is not "valid" | new display-truth test |

Plus the standing gates: openapi-drift, kong-route-coverage, spa-scopes, invariant-registry, migration-compat,
css-classes-exist, display-truth, table-design, scroll-design, and axe over every new route × locale × theme.

---

## 10. Build order

1. **Master lists** — portal section + editor over the existing endpoint. No schema, no new service.
2. **Document validity** — `libs/validity` mirrored; two nullable columns; the sweeper reads configured
   thresholds instead of its constant.
3. **Routing & SLA rules** — the engine's table and authoring surface, on the family that cannot change a
   decision.
4. **Pre-auth trigger rules** — additive-only, on the now-proven rule infrastructure.
5. **Auto-approve** — last, behind the kill switch, once the surrounding machinery has been in use.

Each step ships usable on its own.
