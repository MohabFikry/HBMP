---
name: Policy & Eligibility Engine
description: Models Mersal HBMP policy/coverage/limits and computes real-time benefit eligibility (Eligible / Ineligible / NeedsAuthorization) with minimum-necessary result cards and eligibility snapshots. Use when building or reviewing eligibility, coverage, benefit-limit, or "can this beneficiary receive service X now?" logic.
---

# Policy & Eligibility Engine

## Purpose
Provide the deterministic, auditable "benefit spine" that gates every visit: given a beneficiary,
a coverage, and a requested service, decide **Eligible / Ineligible / NeedsAuthorization** in real
time from policy validity, member status, and remaining limits — and expose only what each caller
is allowed to see (FR-ELG-001..009).

## When to use / when not to use
- **Use when:** implementing/reviewing the eligibility service or API; modelling `policy`,
  `coverage`, `coverage_limit`, `benefit_category`; computing remaining limits; producing the
  Reception eligibility result card; snapshotting decisions onto encounters; wiring cache
  invalidation on consume/dispense/status changes; adding manual override.
- **Do not use for:** the approval/authorization workflow itself (that is the Approvals engine —
  eligibility only *flags* `NeedsAuthorization`); order/prescription consume mechanics (see the
  clinical/consume skills); appointment slot logic.

## Mersal domain knowledge & rules
- **Eligibility is derived, never stored as opinion.** Decision = f({policy validity window} +
  {beneficiary status} + {coverage category} + {remaining limits} + {required authorizations}).
  Same inputs must always yield the same verdict (FR-ELG-002).
- **Three canonical decisions:** `Eligible`, `Ineligible`, `NeedsAuthorization`. (The FR layer also
  speaks of a `Partial` verdict; persist the snapshot `decision` enum as one of the three canonical
  values and carry partiality inside `limit_state`.) Always attach machine reason codes.
- **Member gate:** only a beneficiary in status `Active` can be `Eligible`. `Pending / Suspended /
  Expired / Blocked / Inactive` → `Ineligible` with the status as reason. (See ../../23 §1.)
- **Policy/coverage gate:** `policy.status = Active` and `today ∈ [effective_from, effective_to]`;
  a matching `coverage` row for the requested `benefit_category` (LAB / IMAGING / PHARMACY /
  CONSULT / REFERRAL), also within its effective window.
- **Limit types:** `Annual`, `PerEncounter`, `Lifetime`, `Count`. Each `coverage_limit` carries
  `limit_value`, `consumed_value` (accumulator, `CHECK consumed ≤ limit`), optional `currency_code`
  for monetary limits, and a `reset_period` of `None / Monthly / Quarterly / Yearly`.
- **Remaining = limit_value − consumed_value**, recomputed per reset window. Exhausted limit →
  `Ineligible` (limit reached) unless the service can be authorized → `NeedsAuthorization`.
- **Gated services** (high-cost/controlled) always resolve to `NeedsAuthorization` and route the
  user to initiate an authorization (FR-ELG-006); they never silently pass.
- **Manual override** (Case Manager / Medical Director) can force eligibility for humanitarian edge
  cases — **mandatory reason + audit**, never silent (FR-ELG-007).
- **Reusable service:** one implementation callable by Reception, Call Center, and provider portals
  — do not duplicate the math per portal (FR-ELG-008).

## Key entities, states & invariants
- `policy` → `coverage` (per beneficiary + `benefit_category`) → `coverage_limit` (1..n).
- `eligibility_snapshot`: `decision`, `limit_state` (jsonb), `computed_at`, `expires_at` (cache TTL,
  must be `> computed_at`), `version_hash` (staleness guard). Snapshot the inputs + result and
  **attach to the resulting encounter** (FR-ELG-005) for dispute resolution.
- **Coverage decrement is transactional with consumption** (FR-INV-006): `consumed_value` moves in
  the *same* transaction as an order-line consume or prescription dispense — limits and usage can
  never drift apart. The eligibility engine reads these accumulators; it does not decrement them.
- **Cache invalidation:** any event that changes an input — member status change, policy renew/
  suspend/expire, coverage edit, **consume/dispense**, authorization decision — must invalidate or
  bump `version_hash` so the next read recomputes. A cached snapshot may be served read-only and
  **flagged as stale** during a brief outage (FR-ELG-009); never present stale data as live.

## Minimum-necessary result card
Reception/Call Center see a **verdict card only** (FR-ELG-003): identity match, member status,
coverage summary, remaining limits, and the verdict + reason code. **No diagnoses, no EMR, no
clinical data, no underlying policy math.** Reception = T1; Call Center may see T2 coverage balances.
Field-level minimization is enforced at the service, not just the UI.

## How to apply
1. Resolve beneficiary + active coverage for the requested `benefit_category`.
2. Evaluate gates in order: member status → policy/coverage validity → limit remaining → gating rule.
3. Return `Eligible` / `Ineligible` / `NeedsAuthorization` + reason codes + remaining limits.
4. Persist an `eligibility_snapshot` and link it to the encounter (on booking/check-in).
5. Emit/refresh the cache entry with `expires_at` + `version_hash`; subscribe to invalidation events.
6. Render only minimum-necessary fields per the calling role.

## Canonical references
- ../../07-functional-requirements.md (ELG FR-ELG-001..009; INV-006 coverage decrement)
- ../../22-data-dictionary.md (policy / coverage / coverage_limit / eligibility_snapshot; §11 enums)
- ../../23-state-machines.md (member lifecycle §1; authorization lifecycle §5)

## Guardrails
- Never let eligibility mutate coverage or clinical data — it is read/compute only.
- Never expose clinical reasons to Reception/Call Center; verdict + reason code only.
- Never serve a stale snapshot without an explicit "stale/last-known" flag.
- Never bypass the gated-service check to shortcut a `NeedsAuthorization` into `Eligible`.
- Every override and every PII/coverage read is audited (append-only, hash-chained).
