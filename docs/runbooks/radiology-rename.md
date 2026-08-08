# Runbook — the Imaging → Radiology rename (29.1)

> Design: [45 §1](../../HBMP-Design/45-encounter-and-prescription-adjustments.md) · Decision: [ADR-0029](../adr/0029-radiology-rename-procedures-and-chronic-prescribing.md)

This rename touches a role, a scope vocabulary, two provider columns, an order-type enum, a portal base and
every user-facing string. It is deployed in **four steps, on at least two deploys**, and the fourth step is
physically prevented from shipping with the third.

## Why it is not a find-and-replace

Three artefacts outlive the deployment that renames them.

| # | Artefact | Consequence | Handled by |
|---|---|---|---|
| a | **Unexpired access tokens** carry `imaging_tech` and stay valid for the rest of their **300 s** TTL ([token-contract §4](../security/token-contract.md)) | A technician signed in one second before deploy is refused for five minutes | `libs/auth/LegacyRoleAliases.cs` — expands a principal's roles to **both** spellings |
| b | **In-flight outbox events** carry `orderType: "Imaging"`. The outbox is durable, so they relay *after* the switch | A month of radiology volume splits across two reporting dimensions — the report still renders, so nobody notices | `ProjectionMapping`'s `OrderLinesConsumed` arm, kept **permanently** |
| c | **The audit chain is hash-linked and immutable**. Historical rows say `imaging_tech` | Rewriting them breaks every `record_hash` — `AuditVerifier` reports tampering, correctly | `services/audit/Domain/LegacyIdentifierDisplay.cs` — a **permanent** read-time alias; rows are never updated |

A fourth, less obvious one: services are **independently deployable**, so during rollout the already-switched
issuer mints `radiology_tech` at a service that has not been redeployed and still checks `imaging_tech`. That
is why the alias expansion is **bidirectional** rather than a one-way normalisation — case (a) happens once
for 300 s, this happens on every rollout.

## Sequence

### 1 — EXPAND (deploy N)

Nothing is removed; both spellings work everywhere.

```
services/identity/Infrastructure/Migrations/0031_radiology_role_expand.sql
services/orders/Infrastructure/Migrations/0008_radiology_order_type_expand.sql
services/provider/Infrastructure/Migrations/0011_radiology_provider_type_expand.sql
```

Applied by the normal `tools/ci/apply-migrations.sh`. 0031 **asserts** the two roles are scope-identical and
fails the migration otherwise — a rename that quietly changes authority is a privilege change wearing a
rename's clothes.

### 2 — BACKFILL (deploy N)

```
services/identity/Infrastructure/Migrations/0032_radiology_role_backfill.sql
services/orders/Infrastructure/Migrations/0009_radiology_order_type_backfill.sql
services/provider/Infrastructure/Migrations/0012_radiology_provider_type_backfill.sql
```

Identity grants are **added, not moved** (a grant withdrawn under a live token is a 403 mid-procedure, and an
additive backfill needs no compensating migration if the switch is rolled back). Order and provider rows are
**rewritten in place** — a type is a label on a row only its own service writes, and leaving both spellings
would mean every filter needs `IN (...)` forever, with the one that forgets returning a short worklist rather
than an error.

### 3 — SWITCH (deploy N+1)

Code only. Writers emit the new value; the SPA uses the new portal base, permission keys and strings.
**Reads keep accepting both** — pre-switch orders keep `Imaging` in the row for the life of the order.

Note `apps/web/src/config.ts` carries its **own** dual-accept: the SPA reads the raw `roles` claim, so it
never sees `libs/auth`'s expansion. Without `["imaging_tech", "radiology"]` a mid-deploy technician gets
"No portal assigned" — a correct login presented as an account with no role.

### 4 — CONTRACT (deploy N+2, **not before the preconditions hold**)

```
tools/ci/apply-deferred-migrations.sh   # with DEFERRED_FILTER=radiology
```

The three contract migrations live under `Migrations/deferred/`, which the normal runner does not glob. That
directory **is** the window: a contract migration cannot ship with the switch, because "later" has to be
enforced by something other than an intention.

**Preconditions — verify, do not assume:**

- [ ] **> 300 s** since the switch deploy completed rolling out to *every* replica (not since it started).
      No token naming `imaging_tech` can still be inside its validity.
- [ ] **Outbox drained** of pre-switch events: depth zero, and the oldest undelivered message newer than the
      switch timestamp.
- [ ] The phase-24 **event-symmetry gate** is green.

**Then, in the same change, remove the code-side dual-accept.** Each deferred migration's banner lists its
own removals. A half-contracted rename — DB narrowed, code still dual — is fine; the reverse is the one state
with **no working spelling**. Emptying `LegacyRoleAliases.Aliases` turns `WindowOpen` false and fails
`LegacyRoleAliasTests`, which is the canary telling you the rest of the list is now due.

## What is deliberately NOT renamed

| Left alone | Why |
|---|---|
| The **benefit category** `IMAGING` | A *coverage* vocabulary — a CHECK in policy 0001 and eligibility 0006, a seeded row, a value inside every plan's limits, and the key claims and interop map against. Design 45 §1 renames the role, scopes, provider type, order type, events, portal base and UI strings; it does not name this one, and renaming it would rewrite live benefit accumulators to chase a label. `BenefitCategoryMap` is the seam. |
| **Historical audit rows** | Hash-chained. See (c) above. Permanent display alias only. |
| **OAuth scopes** | Design 45 §1's table says `imaging:*` → `radiology:*`, but no OAuth scope on this platform is spelled that way — the technician's capabilities are `orders:read` / `orders:consume`, shared with the lab bench. The `imaging.*` strings that exist are the SPA's **client-side permission keys**, which *are* renamed at the switch. Recorded in ADR-0029 rather than silently resolved. |

## Rollback

- **Before the contract step:** roll the code back. Both spellings still work in the DB and both are still
  granted, so a rollback needs no compensating migration. This is the entire reason the backfill is additive.
- **After the contract step:** re-apply `0031` + `0032` (both idempotent) to restore the legacy role and its
  grants, then roll the code back. The order-type and provider CHECKs must be re-expanded first.
