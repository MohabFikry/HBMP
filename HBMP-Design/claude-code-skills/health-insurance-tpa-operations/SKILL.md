---
name: Health Insurance & TPA Operations
description: Encodes Mersal-as-Third-Party-Administrator workflows — eligibility verification, member management, prior authorization / medical necessity, utilization management, provider network & tariffs, and the benefits/rules spine. Use when working on TPA operational workflows, eligibility, authorizations, benefit administration, or utilization management on HBMP.
---

# Health Insurance & TPA Operations

## Purpose
Mersal operates as a **Third-Party Administrator (TPA)** for refugee/beneficiary health benefits: it administers coverage, verifies eligibility, authorizes controlled services, manages the provider network, and controls utilization against funded limits — without being the underwriter. This skill gives Claude Code the TPA operating model so features it builds behave like real benefit administration, not a generic EMR.

## When to use / when not to use
- **Use when:** implementing eligibility checks, member lifecycle/enrollment, prior authorization and medical-necessity review, utilization management, provider onboarding/contracts, tariff administration, or the benefits/rules engine glue.
- **Do not use for:** pharmacy-specific DUR/formulary mechanics (use `pbm-adjudication-engine`); provider-record isolation internals (use `provider-network-management`); post-fulfillment financial settlement (use `medical-claims-engine`); declaratively expressing one rule (use `healthcare-business-rules-engine`).

## Mersal domain knowledge & rules
- **Eligibility is the spine.** A single reusable eligibility service answers "can this beneficiary receive service X now?" returning `Eligible | Ineligible | NeedsAuthorization | Partial` with reason codes (`../../07-functional-requirements.md` FR-ELG-001/008). It is **derived from** {policy validity window + beneficiary status + coverage category + remaining limits + required authorizations} (FR-ELG-002) — deterministic and auditable, never ad hoc.
- **Minimum-necessary eligibility result.** Reception/Call Center see an eligibility *result card* — identity match, status, coverage summary, verdict — and **no diagnoses or clinical data** (`../../11-permission-matrix.md` §3.2 Reception clinical = all ❌; FR-ELG-003). Finance sees verdict/financials masked, never diagnosis.
- **Snapshot every decision.** The eligibility decision (inputs + result + timestamp + `version_hash`) is snapshotted and attached to the resulting encounter (FR-ELG-005; `../../22-data-dictionary.md` §4.1). Snapshots are cache-first in Redis, invalidated by `CoverageLimitChanged`/`OrderConsumed`/`DispenseCompleted`/`BeneficiaryStatusChanged`; degrade to a stale-flagged last-known verdict if the live service is briefly down (FR-ELG-009, `../../16-service-architecture.md` §9/§13).
- **Member lifecycle:** `Pending → Active → (Suspended | Expired | Blocked | Inactive)` with reinstate/renew/reactivate paths (`../../23-state-machines.md` §1). Suspension (non-payment/compliance) and Block (fraud) require mandatory reason; Block needs Director-level justification. Only `Active` members with in-window coverage are eligible.
- **Coverage limits are typed and reset-scoped:** `limit_type ∈ {Annual, PerEncounter, Lifetime, Count}`, `reset_period ∈ {None, Monthly, Quarterly, Yearly}`, with `consumed_value ≤ limit_value` enforced by CHECK and decremented transactionally at consume/dispense (`../../22-data-dictionary.md` §3.3). Utilization management reads `limit_value − consumed_value`.
- **Prior authorization / medical necessity.** Gated (high-cost/controlled) services require an authorization before the order/prescription can go `Active`. Lifecycle: `Draft → Submitted → UnderReview → (Approved | PartiallyApproved | Rejected | InfoRequested)` plus `Overridden`, `EmergencyApproved`, `Expired` (`../../23-state-machines.md` §5). Reviewers (`medical_approval`/`medical_director`) may read EMR/notes/reports **only under purpose-binding** (`PUR` = `utilization_review` with a linked approval case) — this is the one role allowed clinical visibility, and it is scoped, not blanket (`../../11-permission-matrix.md` §3.2, §6.5).
- **Governance rules:** separation of duties — a clinician cannot approve their own request (`SOD`, FR-AUTH-011); `EmergencyApproved` fast-tracks service now but **requires retrospective review**; `Overridden` (Medical Director on a rejected case) requires mandatory justification and elevated audit. On approval, the linked order/rx/appointment is **auto-unblocked** via `AuthorizationDecided` event (FR-AUTH-008).
- **Utilization management** = watch approval TAT, limit burn-down, duplicate-order rate (target ~0), and anomalous access; feeds `utilization vs limit` reporting per policy/coverage (FR-RPT-005). Approvals are the cost-control gate; tariffs bound per-service spend.
- **Provider network & tariffs** are the fulfillment backbone: contracts link providers to covered services with `agreed_price` tariffs used to price benefits and route orders (see `provider-network-management`).

## Key entities, states & invariants
- **Entities:** `beneficiary`, `policy`, `coverage`, `coverage_limit`, `benefit_category`, `eligibility_snapshot`, `authorization`, `authorization_decision`, `referral`, `provider_contract`.
- **Invariants:** eligibility is derived + snapshotted, never stored as a mutable verdict; limits decrement only inside the consume/dispense transaction; authorization decisions are append-only; break-glass/override needs extra justification + special audit; tenant/provider isolation on every read; immutable audit on every eligibility read, decision, and status change.

## How to apply
1. For any point-of-care gate, call the eligibility service; act on `NeedsAuthorization` by routing to create an `authorization`, never by silently allowing service.
2. Compute eligibility from the five canonical inputs; return reason codes; snapshot the result to the encounter.
3. Keep all TPA-facing operational screens minimum-necessary — no clinical fields for Reception/Call Center/Finance.
4. Gate high-cost/controlled services behind authorization; enforce SoD, emergency retrospective review, and override justification.
5. Read remaining limits from the accumulator; never let utilization logic double-count against the consume/dispense decrement.
6. On any decision, emit the domain event that auto-advances the linked artifact and write immutable audit.

## Canonical references
- `../../07-functional-requirements.md` (§2 ELG, §7 AUTH, §13 INV)
- `../../11-permission-matrix.md` (Reception ❌ clinical; Approval/Director `PUR`; `SOD`, `BG`)
- `../../23-state-machines.md` (§1 member, §5 authorization)

## Guardrails
- Never expose diagnosis/clinical data on eligibility or scheduling surfaces; Reception clinical access is zero.
- Never approve without SoD clearance; never fast-track an emergency approval without flagging retrospective review; never override without mandatory justification + elevated audit.
- Never derive eligibility from anything but the five canonical inputs; never treat a stale cache as authoritative for consumption.
- Never decrement limits outside the fulfillment transaction; utilization/reporting reads the accumulator only.
