# Phase 27 — Approval engine, benefit lists, rules & the Approval Supervisor portal

**Goal:** Turn approvals from a purely human worklist into an **engine** — versioned benefit lists (formulary / exclusion / escalation) attachable to payer, policy, plan or group; supervisor-authored rules with a closed vocabulary, simulation and dual control; a real reason-code vocabulary; and step-2 authoritative evaluation on both the order/prescription path and the adjudication path.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Design: [`../43-approval-engine-and-prescribing-support.md`](../43-approval-engine-and-prescribing-support.md)

> **Sequencing: run after phase 26.** Phase 26 builds the validation engine with a *benefit pre-check seam* that always returns `NotChecked`. This phase implements that seam for real — in the **same library**, so step 1 and step 2 share one evaluator rather than two implementations that drift.
>
> **What exists today:** approvals is a well-built human worklist with an append-only decision ledger — and **no rules engine at all** (`Domain/DecisionRules.cs` is 32 lines: blank-check, subset-check, TAT, SLA). There is **no formulary, no drug list, no exclusion list** anywhere. `benefit_rule.exclusions jsonb` exists, is parsed into a DTO, and is **never evaluated against anything**. The "formulary service" returns any drug sharing an ATC-5 code, `.Take(8)`, consulting no policy. Rejections have **no reason-code vocabulary**, only free text.

## Skills to activate
> Superpowers: **brainstorming** before 27.2 (the rule vocabulary is a design fork worth exploring properly), **writing-plans** for the migration/precedence sequencing, **test-driven-development** for 27.2–27.3 (the evaluator is pure logic).
> Project skills: `mersal-platform-architect`, `refugee-healthcare-management` (always-on), `healthcare-business-rules-engine`, `pbm-adjudication-engine`, `policy-eligibility-engine`, `health-insurance-tpa-operations`, `healthcare-uiux-designer`.

## Context — read first
- [`../43-approval-engine-and-prescribing-support.md`](../43-approval-engine-and-prescribing-support.md) — **AUTHORITATIVE**: §0, §1 (benefit blocks / clinical warns), §3 (lists), §4 (rules), §5 (two-step), §8 (invariants), §9 (decisions D2, D6).
- [`../38-policy-member-administration.md`](../38-policy-member-administration.md) (plan-version immutability discipline to copy) · [`../36-claims-management.md`](../36-claims-management.md) (denial codes must share the vocabulary) · [`../40-user-access-model.md`](../40-user-access-model.md) (authority vs reach).
- **Existing code:** `services/approvals/**` (esp. `Domain/{DecisionRules,AuthorizationWorkflow}.cs`, `Api/{Worklist,Decisions,BreakGlass}.cs`), `services/policy/{Infrastructure/Migrations/0005_pas_plan.sql,Domain/CoverageDetail.cs}`, `services/pharmacy/Domain/RxRouting.cs` (the config stand-in whose comment says approvals was meant to own this), `libs/authz/ApprovalsPolicies.cs`, `services/identity/Infrastructure/Migrations/0001_identity.sql:204-206`.
- **Run DB-gated tests with `./dotnet.sh test --with-db`.**

## THE INVARIANTS
1. **Benefit rules block; clinical checks only warn.**
2. Lists and rules are **versioned, effective-dated, immutable once Active**.
3. **Deny requires a reason code** from a controlled vocabulary.
4. **No rule activates without a recorded simulation run.**
5. Precedence is deterministic: **group → policy → plan → payer**, and **exclusion beats formulary**; escalation never blocks.
6. Every decision records the **rule version ids and list versions that fired**.
7. **Step 1 is untrusted**; step 2 re-evaluates server-side from current state.
8. The engine **owns no clinical data** and never becomes a second route to PHI.

---

## Prompts

### 27.1 — Benefit lists: one model, three kinds
```text
Read ../43 §3. Build ONE concept, not three tables — formulary, exclusion and escalation are the same
shape and three implementations will drift.

MIGRATION (policy schema — lists are benefit administration, not clinical):
- benefit_list: list_id, tenant_id, kind CHECK IN ('Formulary','Exclusion','Escalation'),
  code varchar(32), name_en, name_ar, description, owner_user_id, + audit/soft-delete/history.
- benefit_list_version: version_id, list_id, version_no, status CHECK IN ('Draft','Active','Retired'),
  effective_from date NOT NULL, effective_to date NULL, activated_by, activated_at, notes.
  IMMUTABLE ONCE ACTIVE — enforce with a trigger the way plan_version already does (../38); editing an
  Active version is impossible, you create the next one.
- benefit_list_item: item_id, version_id, item_type CHECK IN ('Drug','AtcClass','IcdCode','ServiceCode'),
  item_value varchar(64), note. Index (version_id, item_type, item_value).
  AtcClass entries let "all systemic quinolones" be ONE row instead of ninety — resolve ATC prefixes at
  evaluation time, do not expand them into rows at authoring time (the catalogue changes under you).
- benefit_list_attachment: attachment_id, version_id, scope_type CHECK IN ('Payer','Policy','Plan','Group'),
  scope_id, valid_from, valid_until, precedence int, attached_by, + history.
- Tenant RLS + *_history twins throughout.

RESOLUTION (pure domain, test-first):
- Effective lists for a member at a date = attachments matching group -> policy -> plan -> payer,
  most specific wins; within a level EXCLUSION BEATS FORMULARY.
- Formulary semantics: if ANY Active formulary is attached, a drug NOT on it is out-of-formulary
  (Deny with alternatives). If NO formulary is attached, everything is in scope — absence of a
  formulary must never mean "deny everything".
- Escalation: matching items route to a named queue/reviewer tier. NEVER blocks.
- The evaluator returns the LIST VERSION IDS that produced each outcome — an explanation with no
  version reference is unusable six months later.

API (policy-service, scopes policy:read / a new benefit:list:write):
CRUD on lists/versions/items/attachments; POST /benefit-lists/{id}/versions/{v}/activate;
GET /members/{id}/effective-lists?asOf= (the resolution, for the UI and for the engine).
ACCEPTANCE: a UNHCR formulary attached to a GROUP demonstrably changes the outcome for a member of that
group and not for others; an Active version cannot be edited; exclusion beats formulary; no attached
formulary != deny-all.
TESTS: precedence matrix across all four scope levels, exclusion-over-formulary, ATC-prefix matching,
effective-dating boundaries, immutability trigger.
```

### 27.2 — The rule engine: closed vocabulary, versioned, simulated
```text
Read ../43 §4. Use the superpowers brainstorming skill on the fact/operator vocabulary before coding —
this is the decision that determines what the supervisor can and cannot express for years.

HARD CONSTRAINT: a CONDITION BUILDER over a CLOSED vocabulary. No free expression evaluation, no
scripting, no dynamic compilation. An editable expression that executes server-side is a
remote-code-execution surface authored by a non-engineer. If a needed condition is not expressible,
the vocabulary gains a fact — reviewed and released — it does not gain an escape hatch.

MIGRATION (approvals schema):
- rule: rule_id, tenant_id, code, name_en/ar, description, category, owner_user_id, + audit/history.
- rule_version: version_id, rule_id, version_no, status CHECK IN ('Draft','Simulated','PendingReview',
  'Active','Retired'), priority int, effective_from/to, conditions jsonb, actions jsonb,
  authored_by, reviewed_by NULL, activated_by NULL, simulation_id NULL, + history.
  CHECK: status='Active' REQUIRES simulation_id IS NOT NULL AND reviewed_by IS NOT NULL AND
  reviewed_by <> authored_by  ← dual control and mandatory simulation enforced AT THE DATABASE, not by
  a service method someone can forget to call (../43 D6, invariant 4).
- rule_simulation: simulation_id, rule_version_id, window_from, window_to, evaluated_count,
  would_change_count, sample jsonb, ran_by, ran_at.
- decision_rule_trace: decision_id, rule_version_id, outcome, matched_at — which rules fired on which
  decision. Append-only.
- reason_code: code, category, name_en/ar, member_facing_text_en/ar, is_deny bool, retired.
  SEED IT — approvals has NO reason vocabulary today, only free text (Decisions.cs:195-200). Claims
  denials (../36) must draw from THIS SAME table; two vocabularies for the same refusal is how a member
  gets two different explanations for one event.

FACTS (closed set, versioned): drugId, atcCode, icdCode, serviceCode, benefitCategory, requestedQuantity,
requestedDurationDays, estimatedCost, memberAge, memberSex, planId, groupId, payerId, networkTier,
providerId, branchId, cumulativeUtilisationForCategory, remainingLimit, isOnList(listCode),
priorAuthExistsFor(serviceCode), daysSinceLastDispense(drugId), prescriberSpecialty.
OPERATORS: eq, neq, gt, gte, lt, lte, in, notIn, matchesAtcPrefix, between, exists, notExists + and/or/not.
ACTIONS (closed): Allow · Deny(reasonCode) · RequirePreauth · Escalate(queue) · RequireDocument(kind) ·
CapQuantity(n) · WarnOnly(message).

EVALUATION: deterministic priority order; FIRST TERMINAL ACTION WINS (Deny/Allow terminate; RequirePreauth,
Escalate, RequireDocument, CapQuantity accumulate); conflicting rules detected AT AUTHORING TIME with a
clear message, not discovered in production. Every evaluation returns the full trace.

SIMULATION (the highest-value feature here): POST /rules/{id}/versions/{v}/simulate {from,to} replays the
version against real historical authorizations/prescriptions in that window and reports: evaluated,
would-change, breakdown by action, and a sample of changed cases with before/after. A rule that would
have denied 40% of last month's requests must never reach production unseen.
ACCEPTANCE: an Active rule without simulation+independent-reviewer is rejected BY THE DATABASE;
conflicting priorities are caught at authoring; simulation reports a correct would-change count against
seeded history; every decision carries a trace.
TESTS: evaluation order, terminal-action semantics, each action, conflict detection, the DB constraint
(attempt a direct SQL activate without a simulation and assert it fails), simulation accuracy.
```

### 27.3 — Step 2: authoritative evaluation on both paths
```text
Read ../43 §5. Phase 26 left a benefit pre-check SEAM returning NotChecked. Implement it — IN THE SAME
LIBRARY (libs/clinical-validation or wherever 26.3 put it) so step 1 and step 2 share ONE evaluator.

- Order/prescription path: on submit, the server RE-EVALUATES lists + rules from current member state,
  current list versions and current rule versions. The client's step-1 findings are display state only.
  THE TEST: submit a payload asserting a clean verdict for a drug that a rule denies; assert the server
  denies it anyway. Pin it in docs/quality/invariant-registry.yaml.
- Adjudication path: when an authorization is opened for review, the engine produces a RECOMMENDATION
  with its trace — approve/deny/escalate/require-doc plus the rule and list versions that fired. It is a
  RECOMMENDATION: the human decides, and their decision is recorded against the recommendation so
  disagreement rates become measurable. Do NOT auto-decide in this phase.
- authorization gains: engine_recommendation, engine_trace jsonb, reason_code (FK to the vocabulary),
  and Decisions.Decide REQUIRES a reason code on Deny/Partial (422 without one). Free-text rationale
  stays, alongside — not instead of.
- Escalation actions route to the queue named by the rule; the worklist filter gains queue.
- RxRouting (pharmacy/Domain/RxRouting.cs) is a config stand-in whose own comment says approvals should
  own this — RETIRE it in favour of the engine, or state in the ADR why it survives.
ACCEPTANCE: forged client verdict ignored; every Deny carries a reason code; reviewers see the
recommendation + trace; disagreement is recorded; escalation routes.
TESTS: the forged-payload test, reason-code enforcement, trace completeness, recommendation-vs-decision
recording, step-1/step-2 divergence surfaces rather than errors.
```

### 27.4 — Approval Supervisor role & portal
```text
Read ../43 §9 D2. New role `approval_supervisor`: medical_approval's exact scopes (auth:read, auth:review,
auth:decide, auth:manual, auth:emergency, policy:read, notification:read) PLUS authoring scopes:
`benefit:list:write`, `rule:author`, `rule:activate`, `masterdata:read`.

SPLIT THE AUTHORING SCOPES FROM THE PHI SCOPES. A policy analyst who authors lists and rules should not
need to read patient records; keep `rule:author`/`benefit:list:write` independent of auth:review so the
two can be separated later without re-cutting the role. Add the test asserting the authoring scopes
carry no PHI reach on their own.
DUAL CONTROL: `rule:activate` must be held by someone OTHER than the author for a deny-capable rule —
the DB constraint in 27.2 enforces it; the SoD engine surfaces it as a 409 with the conflict reason.

PORTAL (base `approvals-admin`, or extend the existing approvals portal with an admin group):
- Worklist etc. as medical_approval has today — do not fork the screens.
- Master lists browser (drugs/ICD/ATC/services, read-only, searchable — reuse the 26.2 search).
- Benefit lists: create/version/activate; item editor with drug and ATC-prefix pickers; attachment
  editor (payer/policy/plan/group + validity + precedence) with a live "who does this affect?" count.
- Rule builder: condition tree over the closed vocabulary (no free-text expression box anywhere),
  action picker, priority, conflict warnings inline, and a mandatory Simulate step whose result must be
  reviewed before Activate becomes available. Activate is disabled for the author of the version.
- Reason-code administration.
- All bilingual AR/EN with RTL, WCAG 2.2 AA, axe clean against POPULATED fixtures (add DevApiClient
  fixtures from the start — do not repeat the vacuous-a11y problem the policy screens have).
- Policy configuration portal (../38) gains the SAME attachment editor — one component, two doors, so a
  formulary attached from either place behaves identically.
ACCEPTANCE: supervisor can build a UNHCR formulary, attach it to a group, simulate a rule, and see the
would-change report; cannot activate their own deny rule; authoring scopes alone grant no PHI.
TESTS: scope-split test, self-activation refusal, attachment parity between the two portals, axe EN+AR.
```

### 27.5 — Docs, seeds, registry
```text
- ../11 gains the new scopes + the supervisor row; ../10 gains approval_supervisor; ../22 gains all new
  tables; ../23 gains the engine states on the authorization lifecycle; ../36 references the SHARED
  reason-code vocabulary; ../38 gains the attachment editor; ../17 gains the endpoints;
  00-README-INDEX + README gain doc 43 (count -> 43); BUILD-STATUS gains 27.1-27.5.
- SEEDS for demo: a "UNHCR Essential Medicines" formulary attached to a group; an exclusion list with
  a cosmetic-drug ATC class; an escalation list for high-cost oncology; three rules exercising Deny,
  RequirePreauth and Escalate; a reason-code set.
- Registry entries: exclusion-beats-formulary, no-activation-without-simulation, deny-requires-reason-code,
  server-ignores-client-verdict, authoring-scopes-carry-no-PHI.
- ADR-0027: one list model for three kinds; closed rule vocabulary and why no expression language;
  mandatory simulation; recommendation-not-auto-decision in v1.
ACCEPTANCE: seeds make every path demonstrable; docs true; registry entries have named tests.
```

---

## Guardrails
- **No expression language. Ever.** Closed fact/operator/action vocabulary only.
- **No auto-decision in v1** — the engine recommends, a human decides, and the disagreement is measured. Revisit auto-approval only once that rate is known.
- **Absence of a formulary is not deny-all.**
- **Exclusion beats formulary**; escalation never blocks.
- **Deny without a reason code is a 422.**
- Activation requires simulation **and** a second person — enforced at the database, not in a service method.
- Step 2 never trusts step 1.
- Every list, rule, attachment, evaluation, recommendation and decision is audited; no hard deletes; `*_history` twins throughout.
- Full suite green after each sub-prompt (`--with-db`), including the untouched min-necessary, RLS, SoD and approvals suites.

## Done when
- [ ] One `benefit_list` model serves formulary, exclusion and escalation; versioned, effective-dated, immutable once Active; attachable to payer/policy/plan/group with deterministic precedence; a UNHCR formulary on a group changes outcomes for that group only.
- [ ] Rule engine with a closed vocabulary, priority ordering, authoring-time conflict detection, versioning, and a **database constraint** making activation impossible without a simulation and an independent reviewer.
- [ ] Reason-code vocabulary seeded and **shared with claims**; every Deny carries one.
- [ ] Step 2 re-evaluates server-side and ignores client verdicts, proven by the forged-payload test; adjudicators see a recommendation + full trace; decisions record agreement/disagreement.
- [ ] `approval_supervisor` seeded with medical_approval's scopes plus authoring scopes; authoring scopes alone carry no PHI; an author cannot activate their own deny-capable rule.
- [ ] Supervisor portal: master-list browser, list/version/item/attachment editors with impact counts, rule builder with mandatory simulation, reason-code admin; attachment editor shared with the policy portal; axe clean EN+AR on populated fixtures.
- [ ] Docs 10/11/17/22/23/36/38 updated, doc 43 indexed, ADR-0027 merged, registry entries have named tests.
