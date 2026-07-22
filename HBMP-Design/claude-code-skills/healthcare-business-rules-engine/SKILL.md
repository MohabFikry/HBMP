---
name: Healthcare Business Rules Engine
description: Express Mersal benefit, eligibility, authorization, and formulary logic as declarative, versioned, auditable rules separated from application code — rule categories, evaluation order, outcome auditability, and policy-engine wiring (OPA/Cerbos). Use when encoding, reviewing, or refactoring any benefit, eligibility, coverage, pre-auth, step-therapy, quantity-limit, or DUR business rule.
---

# Healthcare Business Rules Engine

## Purpose
Give Claude Code a consistent way to express Mersal's benefit/clinical **business rules declaratively** rather than scattering `if` statements through services. Rules — eligibility, coverage limits, pre-auth triggers, step therapy, quantity limits, DUR — are policy, and policy changes often; it must be versioned, testable, auditable, and separable from code. Mersal already runs a **policy engine (OPA/Cerbos)** for authorization; the same discipline applies to benefit rules.

## When to use / when not to use
- **Use when:** encoding or reviewing any benefit/eligibility/authorization/formulary rule; deciding rule evaluation order; making rule outcomes auditable; versioning rule sets; or separating policy from application code.
- **Do not use for:** the domain-specific *content* of a rule set alone — pair this with the domain skill (`health-insurance-tpa-operations`, `pbm-adjudication-engine`, `medical-claims-engine`, `provider-network-management`) for the actual Mersal values.

## Mersal domain knowledge & rules
- **Rule categories.** Model each rule as one of: **Eligibility** (status + policy window + coverage category), **Coverage limit** (`Annual/PerEncounter/Lifetime/Count` with `reset_period`), **Pre-auth trigger** (by cost threshold or specific gated service/category), **Step therapy** (documented failure of a preferred same-ATC-class agent first), **Quantity limit** (per-period cap, DDD-informed), and **DUR** (drug–drug/drug–allergy by severity `Minor/Moderate/Major/Contraindicated`). These map directly to the enums in `../../22-data-dictionary.md` §3.3/§10.5/§11.
- **Deterministic evaluation order.** Benefit adjudication runs a fixed pipeline: `1 eligibility → 2 coverage-category match → 3 pre-auth trigger → 4 clinical/DUR safety → 5 step therapy → 6 quantity/coverage limit → 7 pricing`. Short-circuit on a hard fail but collect per-line reasons so **partial** outcomes (`Partial`/`PartiallyApproved`) are precise, mirroring the authorization state machine (`../../23-state-machines.md` §5). Deterministic order = auditable, reproducible decisions (`../../07-functional-requirements.md` FR-ELG-002).
- **Precedence & default-deny.** Follow the platform precedence model: `explicit-deny (field/SoD/env) ▶ break-glass-scoped-allow ▶ ABAC-conditional-allow ▶ RBAC-allow ▶ default-deny` (`../../11-permission-matrix.md` §7). A benefit rule engine likewise **defaults to deny/NeedsAuthorization** when inputs are missing, and a deny/limit rule overrides a permissive one.
- **Rules are data, not code.** Authorization policy ships as **versioned, peer-reviewed, tested Rego/Cerbos bundles deployed via an audited pipeline — no manual edits in production** (`../../11-permission-matrix.md` §6, change-control note). Benefit rules follow the same pattern: rule sets are effective-dated master data (FR-MDM-007), versioned, and evaluated by a policy engine, so a coverage/formulary change is a data/version change, not a redeploy of business logic.
- **Every rule outcome is audited.** Each evaluation writes an append-only record — inputs (minimized), matched rule + version, decision, reason codes, timestamp, correlationId (`../../07-functional-requirements.md` FR-INV-007, FR-AUD-001/002). This is what makes an eligibility snapshot or an adjudication defensible in a dispute.
- **Minimum-necessary inputs.** A rule may only read the fields the evaluating context is permitted to see. A finance/claims rule set must not receive `diagnosis`; a pharmacy DUR rule gets a derived safety flag, not raw lab values (`../../11-permission-matrix.md` §3.2/§4). Rule authoring must respect field-level access, not just object access.
- **Versioning & effective-dating.** Historical decisions must remain reproducible against the rule version in force at decision time; new fields are additive; breaking changes bump the version and dual-run during migration (mirrors event versioning, `../../16-service-architecture.md` §7).

## Key entities, states & invariants
- **Rule shape:** `{ id, category, effective_from/to, priority, condition (attributes), effect (allow/deny/needs-auth/limit), reason_code, version }`.
- **Evaluation inputs (attributes):** beneficiary status, policy window, coverage category + remaining limit, service code + cost, authorization state, drug ATC/interaction/allergy, provider ownership — asserted by trusted sources (token claims, resource attributes, environment) exactly as ABAC attributes are (`../../11-permission-matrix.md` §5).
- **Invariants:** default-deny; deterministic ordering; deny/limit overrides allow; every outcome auditable with matched rule+version; rule sets versioned and effective-dated; historical reproducibility; field-level minimization on rule inputs; no manual production rule edits.

## How to apply
1. Classify the rule into one of the six categories; put it in the right pipeline stage.
2. Express it declaratively (condition → effect + reason_code) as versioned data, not inline code; keep it effective-dated.
3. Evaluate in the fixed order with default-deny and deny-overrides-allow; collect per-line reasons for partial outcomes.
4. Emit an append-only outcome record capturing matched rule id + version and reason codes.
5. Enforce field-level minimization: give each rule set only the inputs its context may see.
6. Ship rule changes through the same versioned, peer-reviewed, tested, audited pipeline as the authz bundles — never hot-patch production logic.

## Canonical references
- `../../07-functional-requirements.md` (§2 ELG deterministic derivation, §12 MDM versioning, §13 INV, §14 AUD)
- `../../11-permission-matrix.md` (§5 ABAC attributes, §6 policy bundles, §7 precedence/default-deny, change-control)
- `../../23-state-machines.md` (§5 authorization — partial/deny outcomes as first-class states)

## Guardrails
- Never bury benefit logic in imperative service code — express it as versioned, effective-dated, engine-evaluated rules.
- Never let a rule read fields the context is denied (no diagnosis in finance rules; derived flags, not raw results, in pharmacy DUR).
- Never default to allow on missing inputs — default to deny/NeedsAuthorization.
- Never skip the outcome audit; always record the matched rule id + version + reason codes.
- Never break historical reproducibility; bump versions and dual-run instead of editing a live rule.
