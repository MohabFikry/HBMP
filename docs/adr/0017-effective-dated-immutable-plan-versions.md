# ADR-0017 — Effective-dated, immutable plan versions (Phase 19.1)

- Status: Accepted
- Date: 2026-07-28
- Deciders: HBMP platform / benefit administration
- Context docs: `38-policy-member-administration.md §2`, `22-data-dictionary.md`, `23-state-machines.md`,
  `07-functional-requirements.md (FR-POL-*)`, `19-audit-strategy.md`.

## Context

A benefit plan says what Mersal pays for. That statement changes — a limit is raised, a category is added, a
pre-authorization threshold moves — and the changes are **not corrections**. Each one is a decision that took
effect on a date, and every claim, eligibility check and authorization decided before that date was decided
under the previous statement.

The obvious model is a mutable `plan` row that administrators edit. It is wrong in a way that only becomes
visible under dispute: once the row is edited, there is no longer any record of what the plan said when the
service was delivered. A beneficiary asking "why was I only paid 500 when I was told 1,000" cannot be answered,
and neither can an auditor asking the same question on Mersal's behalf. The system would be unable to
reconstruct its own decisions.

A softer variant — mutable rows plus an audit trail of edits — is worse than it looks. Reconstructing "the plan
as at 3 March" then means replaying a diff log, which is a second implementation of versioning that only the
audit reader has, while every runtime read still sees today's values.

## Decision

**A plan is a container. A PLAN VERSION is the thing that says anything, it is effective-dated, and once it is
Active it is immutable.**

- `plan_version` carries `effective_from` / `effective_to`, a `version_no`, and a status of
  `Draft → Active → Superseded`.
- Benefit rules (`benefit_rule`, and per-tier cost-share in `benefit_rule_tier`) hang off the VERSION, never
  off the plan.
- **Draft is the only writable state.** A database trigger enforces it, not just the service layer: rules
  cannot be inserted, updated or deleted under a non-Draft version.
- Changing an Active version is done by **amend**: clone it to a new Draft, edit that, activate it. Activation
  supersedes the previous version and sets its `effective_to`.
- Activation runs a validation pass and refuses an incoherent version (a covered category with no limit and no
  explicit unlimited, a pre-auth threshold above the limit it guards, a tier priced on a retired tier).
- Every adjudicating read resolves **the version in force on the service date**, not the latest version.

## Consequences

- "What did this plan say on 3 March" is a query, not a reconstruction. Eligibility, approvals and claims all
  answer it the same way, because they all use the same resolver.
- An administrator cannot fix a typo in place. This is the cost, and it is deliberate: a typo in a benefit
  limit and a decision to change a benefit limit look identical in the data, so the system treats both as a
  new version. The version timeline and the version diff (19.6) exist to make that cheap rather than painful.
- Coverage generated from a version records `source_plan_version_id`, so a member's entitlement points back at
  the exact configuration that produced it — including after the version is superseded.
- A plan version carries **no PHI whatsoever**, which is why `policy:read` can be broad. Minimum-necessary
  bites at the member level, not here.

## Alternatives rejected

- **Mutable plan + audit log.** Reconstruction lives only in the audit reader; runtime reads still see today.
- **Copy-on-write per policy.** Each policy holding its own snapshot removes the shared vocabulary that
  eligibility and claims adjudicate against, and makes a fleet-wide correction impossible.
- **Soft-delete + re-create instead of amend.** Loses the lineage (`superseded_by_version_id`), so the
  timeline becomes a set of unrelated versions with no stated relationship.
