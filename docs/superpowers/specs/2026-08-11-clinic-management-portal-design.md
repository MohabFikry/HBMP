# Clinic Management portal — architecture audit and redesign

> Portal: `base: "branch"`, shared by `branch_coordinator` and `clinics_manager`.
> Design set: [42-branch-management.md](../../../HBMP-Design/42-branch-management.md) ·
> [37-branch-scoping-and-clinical-sensitivity.md](../../../HBMP-Design/37-branch-scoping-and-clinical-sensitivity.md) ·
> [40-user-access-model.md](../../../HBMP-Design/40-user-access-model.md)
> Implementation ADR to update: [0029-branch-management.md](../../adr/0029-branch-management.md)

Phase 25 built this portal. This document records what an audit of it found, and the design that
closes the gaps: an authorization hole on the write path, an availability rule nobody can read or
edit, no capacity model at all, and no way for the people who run a clinic to see who changed it.

---

## 1. Audit findings

Ordered by severity. Every claim below was verified against the code at the cited line.

### A1 — the branch write path predates `BranchSetScoped` (authorization, high)

`libs/authz/BranchQueryScope.cs:9-18` documents this exact bug and fixes it **for reads**:

> Every branch-scoped read on the platform was written as `if (branch.Context.ActiveBranchId is { } active)
> q = q.Where(...)`, which is correct for the two modes that existed when it was written and quietly WRONG
> for `BranchSetScoped`.

The write path was never migrated. `services/emr/Api/AppointmentEndpointsShared.cs:19`:

```csharp
public static (Guid? Branch, IResult? Denied) ResolveBookingBranch(BranchScopeState branch, Guid? requested)
{
    if (branch.Context.ActiveBranchId is not { } active) return (requested, null);   // ← here
```

`BranchScopeResolver.cs:55-59` leaves `ActiveBranchId` null for a set-scoped caller who has not
filtered, and carries the permitted set instead. So the caller's `branchId` — a value straight off the
request body — is returned **without ever being tested against `PermittedBranchIds`**.
`DenyIfOutsideBranchAsync` (line 33) fails open in the same way.

A `clinics_manager` granted two clinics can therefore act on all six: close a clinic they do not run,
flag its appointments for reassignment, materialize slots into it, book, check in, no-show and cancel.
The eleven affected call sites are in `Appointments.cs` (5), `RosterExceptions.cs` (2), `Queue.cs` (1)
and `Program.cs` (1).

This violates design 42 §7 rule 2 — *reach is grant-derived, never role-derived; unresolvable reach
matches nothing*. provider-service is not affected: `BranchReachGuard` delegates to
`AbacConditions.InBranchScope`, which knows all three modes.

### A2 — the weekly availability rule has no CRUD

`emr.provider_availability` (migration `0002_appointments.sql:8`) has no `GET`, `PUT` or `DELETE`
anywhere in the codebase. Its only writer is `POST /appointment-slots`, which constructs a **new**
`ProviderAvailability` on every call (`Appointments.cs:121-130`). There is no unique index on the rule's
natural key.

Consequences, all live today:

- Repeated slot materialization accumulates duplicate rules for the same doctor, branch and weekday.
- A rule can never be corrected or retired. The only way to stop a weekly pattern is to leave orphaned
  rules behind and delete the materialized slots by hand.
- `BranchRoster.tsx:15` opens with *"The weekly pattern says when the clinic normally runs"* — a
  sentence about data the screen has no endpoint to fetch. The roster is exceptions-only.

### A3 — there is no capacity model

No occurrence of a per-practitioner daily cap in schema, domain, API or UI. Capacity is implicit in
`slot_minutes × (end_time − start_time)`, which cannot express "Dr Hala takes twenty patients a day
however long the session runs".

### A4 — the only change history is the security audit store, which branch roles cannot read

Licence and roster mutations are audited correctly (`Practitioners.cs:172`, `RosterExceptions.cs:147`).
But the read path is `GET /api/v1/audit/events/{entityType}/{entityId}`, gated on `audit:read`
(`services/audit/Api/Program.cs:82`) — Security, Compliance and the DPO. Branch roles hold none of it,
and should not.

There is no domain-level history for licences, availability or roster exceptions, and no timeline in the
UI. "Who changed this clinic's Tuesday, and when" is currently unanswerable by the person who runs it.

### A5 — five screens bypass the `ApiClient` seam

`branchApi`, `rosterApi` and `inventoryApi` call `http.ts` directly. Every other portal resolves through
`ApiClient`, which `ApiProvider.tsx:19` swaps for `DevApiClient` in fixture mode. The SPA defaults to
fixture mode (`config.ts`, `LIVE = !FIXTURE_MODE`) and there is no MSW or fetch interception in the
tree. So the entire Clinic Management portal errors in the demo bundle, and cannot be rendered in a
screen-level test — which is why the only test covering it, `branch-licence-cues.test.tsx`, exercises
`LicenceStatus` in isolation.

### B1 — `rosterApi.withdraw` calls a route that does not exist

Client (`branchApi.ts:183`): `POST /roster-exceptions/{id}/withdraw`.
Server (`RosterExceptions.cs:167`): `DELETE /roster-exceptions/{id}`.
It is also dead code — no screen calls it — so an exception cannot currently be undone at all.

### UI/UX defects

| | Defect | Evidence |
|---|---|---|
| C1 | The exception form never sends `practitionerId`, so every exception is branch-wide. "Dr Hala is on leave next Tuesday" — design 42 §4's own motivating example — cannot be recorded. | `BranchRoster.tsx:174-181` |
| C2 | A clinics manager cannot create any exception: no active branch, no `branchId` field on the form, no practitioner ⇒ `400 roster-exception-target-required`. The supervisor of six clinics is the one user locked out of the screen. | `RosterExceptions.cs:89` |
| C3 | Branches Overview renders `branchId.slice(0, 8)`. Its comment claims names sit behind `provider:read`; `provider/Api/Branches.cs:20` is `RequireAuthorization()` — any authenticated user — and `useBranchContext.ts:70` already fetches them for the switcher. | `BranchesOverview.tsx:84-86` |
| C4 | "Record renewal" appends a `Card` below the table rather than opening the design system's `Modal`. No focus move, no `role="dialog"`, no Esc, no focus restoration — a WCAG 2.2 focus-management failure on the portal's primary write. | `BranchLicences.tsx:171-181` |
| C5 | Licence Alerts, the "who do I chase" worklist, carries no renew action. The operator must memorise a name and go looking for it on another screen. | `BranchLicences.tsx:252-272` |
| C6 | Shortening a licence expiry strands booked appointments — the server emits `PractitionerLicenceExpired` for exactly that reason (`Practitioners.cs:150-166`) — but the UI applies it with no impact preview, while the roster makes a preview mandatory. | `BranchLicences.tsx:201-211` |

---

## 2. Design

### 2.1 `BranchWriteScope` — one question, three modes, fail closed

New in `libs/authz`, beside `BranchQueryScope` and shaped the same way:

```csharp
public static class BranchWriteScope
{
    /// null ⇒ allowed. A Problem ⇒ 403, audited by the caller.
    public static IResult? RefuseUnlessWritable(ScopeMode mode, IBranchContext ctx, Guid? target);
    public static (Guid? Branch, IResult? Denied) ResolveTarget(ScopeMode mode, IBranchContext ctx, Guid? requested);
}
```

| mode | rule |
|---|---|
| `BranchScoped` | target must equal `ActiveBranchId`; a null request resolves to it. Unchanged behaviour. |
| `BranchSetScoped` | target must be **in** `PermittedBranchIds`. A null target with no filter set is **refused**, not defaulted — the caller must say which clinic. |
| `MemberScoped` / `ProviderScoped` | unrestricted; the target passes through. Unchanged. |

`ResolveBookingBranch` and `DenyIfOutsideBranchAsync` become thin wrappers, so all eleven call sites are
corrected at once and no endpoint restates the rule. The refusal reuses the existing
`urn:hbmp:branch-scope-denied` problem type.

**Why refuse rather than fan out.** A set-scoped manager POSTing a roster exception with no branch could
plausibly mean "all my clinics". Refusing is the fail-closed reading: a request that would close six
clinics must say so, and the UI now has a branch picker (§2.5) to say it with.

### 2.2 Availability as an administered aggregate

Migration `emr/0025_provider_availability_managed.sql`, additive (expand/contract):

```sql
ALTER TABLE emr.provider_availability
    ADD COLUMN IF NOT EXISTS tenant_id   text,
    ADD COLUMN IF NOT EXISTS max_per_day int,          -- NULL = uncapped
    ADD COLUMN IF NOT EXISTS is_deleted  boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS created_at  timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS created_by  text,
    ADD COLUMN IF NOT EXISTS updated_at  timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_by  text,
    ADD CONSTRAINT ck_availability_max_per_day CHECK (max_per_day IS NULL OR max_per_day > 0);

CREATE UNIQUE INDEX IF NOT EXISTS ux_availability_rule
    ON emr.provider_availability (
        tenant_id, provider_id, location_id,
        coalesce(doctor_id, '00000000-0000-0000-0000-000000000000'::uuid),
        coalesce(branch_id, '00000000-0000-0000-0000-000000000000'::uuid),
        day_of_week)
    WHERE NOT is_deleted;
```

`max_per_day` is NULLable so every existing rule keeps its present behaviour on deploy. The unique index
is what stops the duplicate accumulation A2 describes; existing duplicates are collapsed by the
migration keeping the most recent row per key and soft-deleting the rest.

New endpoints in `emr`, `Api/ProviderAvailability.cs`:

| route | scope | notes |
|---|---|---|
| `GET /api/v1/provider-availability` | `appointment:read` | branch-scoped via `BranchQueryScope`; filters `branchId`, `doctorId` |
| `POST /api/v1/provider-availability` | `branch:roster:write` | `409` on the unique key, with the existing rule's id |
| `PUT /api/v1/provider-availability/{id}` | `branch:roster:write` | hours, slot minutes, `maxPerDay` |
| `DELETE /api/v1/provider-availability/{id}` | `branch:roster:write` | soft delete |
| `GET /api/v1/provider-availability/{id}/history` | `appointment:read` | §2.4 |

All writes go through `BranchWriteScope` and write a history row in the same transaction.

`POST /appointment-slots` stops minting rules. It accepts `availabilityId` and materializes from the
stored rule. The legacy inline-rule body is still accepted for one deprecation window, but now
**upserts** on the unique key rather than inserting — so the old call path stops creating duplicates
before it is removed.

### 2.3 Capacity — enforced twice, defined once

`max_per_day` lives on the availability rule, which makes it **per physician, per branch, per weekday**.

> **Stated assumption.** A doctor working Maadi mornings and Dokki evenings holds two rules and
> therefore two caps. This is deliberate: the cap is administered by whoever runs the clinic, and a
> coordinator who reaches one branch must be able to set the cap that applies there without touching
> another clinic's. A network-wide "twenty patients a day across everything" is a different control and
> is out of scope here.

**Generation.** `SlotGeneration.Generate` takes the cap and emits at most `maxPerDay` slots per date
across that rule's windows (the recurring window plus any `AdHocClinic` on the day), after subtraction.
It belongs here because design 42 §7 rule 5 says availability is computed in exactly one place, and a
cap applied anywhere else is a second place deciding whether a slot exists.

**Booking.** The booking validator counts live appointments (`Booked`, `CheckedIn`, `InProgress`) for
(doctorId, branchId, clinic-date in Africa/Cairo) and refuses:

```
409 urn:hbmp:daily-capacity-reached
detail: "Dr … is booked to capacity at this clinic on 2026-09-14 (20 of 20)."
```

Both are needed. Generation keeps the calendar honest so the desk never offers a slot that will be
rejected; the booking check is what survives an ad-hoc clinic, a manual booking, or any path that does
not consume a materialized slot. The count is taken inside the same transaction that holds the slot, so
two concurrent bookings cannot both pass the cap — asserted by a concurrency test alongside the existing
consume/dispense proofs.

### 2.4 Change history — domain tables, not the audit spine

Three append-only tables, written in the same transaction as the change they describe:

| table | migration |
|---|---|
| `provider.practitioner_licence_history` | `provider/0014_practitioner_licence_history.sql` |
| `emr.provider_availability_history` | `emr/0026_availability_history.sql` |
| `emr.roster_exception_history` | `emr/0027_roster_exception_history.sql` |

Common shape:

```
history_id      uuid PRIMARY KEY
tenant_id       text NOT NULL
<subject>_id    uuid NOT NULL
change_kind     varchar(24) NOT NULL     -- Created | Updated | Withdrawn | Renewed
before          jsonb                    -- minimised; NULL on creation
after           jsonb NOT NULL
reason          varchar(300)
actor_subject   text
actor_name      text                     -- resolved at write time, so the timeline reads without a join
occurred_at     timestamptz NOT NULL
```

`actor_name` is captured at write time deliberately. A timeline that resolves names at read time shows
"unknown" for anyone who has since left, and joining to identity from emr would make a scheduling read
depend on the issuer.

`before`/`after` carry only the administered fields — hours, slot minutes, cap, dates, licence number and
expiry. No beneficiary identifiers reach these tables; the impacted-appointment list stays where it is,
behind the same branch scoping as the appointments themselves.

Read at `GET …/{id}/history`, behind the existing `branch:*` and `appointment:read` scopes and the same
branch reach as the record itself. **`audit:read` is not widened.** The hash-chained audit store keeps
doing its evidential job untouched; this is the operational answer to a different question, asked by
different people, and giving clinic staff the compliance trail to answer it would fail minimum-necessary.

The audit event and the history row are written in one transaction. A test asserts that every mutating
route on these three aggregates writes both.

### 2.5 Screens

**Roster & Availability** becomes two bands, in the order a coordinator reasons about them:

1. **Weekly pattern** — a row per (practitioner × weekday) showing hours, slot length, **daily cap** and
   the resulting slot count. Inline edit; a "History" action per row.
2. **Exceptions** — the existing calendar, plus a **Withdraw** action per row, and a **practitioner
   picker** and **branch picker** on the record form.

The branch picker renders only for a set-scoped caller — for a coordinator the branch is their own and
naming it would be a decision they do not have. This is a reach distinction, matching `Section.reachScoped`
in the portal catalog, and it introduces no new permission.

**Practitioners** — renewal moves into `Modal` with focus trap, Esc, and focus restored to the invoking
row (C4). Shortening an expiry first calls `GET /practitioners/{id}/licence-impact?expiry=…`, which
returns the appointments beyond the new date; the save is then gated on the same acknowledged-count guard
the roster uses, and refuses `409 urn:hbmp:impact-acknowledgement-required` if the count moved (C6). A
"History" action per row opens the licence timeline.

**Licence Alerts** — a renew action on every row, opening the same modal (C5).

**Branches Overview** — real branch names, from the `GET /branches` read the switcher already makes (C3).

A shared `<ChangeTimeline>` component renders all three histories: actor name, Cairo-formatted timestamp,
what changed as before → after, and the reason. Bilingual, four-cue where it shows status, axe-clean.

### 2.6 The fixture seam (A5)

`branchApi`, `rosterApi` and `inventoryApi` become interfaces (`BranchApi`, `RosterApi`, `InventoryApi`)
with two implementations:

- `HttpBranchApi` — today's `http.ts` calls, unchanged.
- `DevBranchApi` — fixtures consistent with `DevApiClient`'s existing branch, practitioner and
  appointment data, so a demo shows one coherent clinic rather than two.

Selected through the existing `@dev/fixtures` alias, exactly as `ApiProvider` selects `DevApiClient`. No
new mechanism, and a live build still excludes the fixtures from the bundle.

This is what makes the portal usable in the demo bundle and testable at screen level; the axe route ×
locale × theme sweep then covers these five screens automatically.

---

## 3. Invariants this adds to design 42 §7

11. **The branch write path asks the same question as the branch read path.** One implementation,
    `BranchWriteScope`, three modes, fail closed. A set-scoped caller who names no branch is refused.
12. **One availability rule per practitioner, per clinic, per weekday**, enforced by a partial unique
    index. Slot materialization reads a rule; it never creates one.
13. **A daily cap is enforced at generation and at booking.** Generation keeps the calendar honest;
    booking is what holds when a slot was never materialized.
14. **Every administered change to a licence, an availability rule or a roster exception writes a
    domain history row in the same transaction as the change** — and the audit event as well. Neither
    substitutes for the other.
15. **Operational history is not the audit trail.** `audit:read` is never granted to a branch role.

---

## 4. Acceptance criteria

- [ ] `BranchWriteScope` implemented; `ResolveBookingBranch`/`DenyIfOutsideBranchAsync` delegate to it. A
      test proves a set-scoped caller with two permitted branches is refused `403` on a write naming a
      third, **and** refused when naming none.
- [ ] `ux_availability_rule` in place; the migration collapses pre-existing duplicates; a second
      `POST /provider-availability` on the same key returns `409` naming the existing rule.
- [ ] Availability CRUD reachable at `branch:roster:write`, branch-scoped, audited, history-written.
- [ ] `POST /appointment-slots` materializes from a stored rule and no longer inserts duplicates on the
      legacy path.
- [ ] `maxPerDay` truncates generation per date, and booking the cap+1th appointment returns
      `409 urn:hbmp:daily-capacity-reached`. A concurrency test proves two parallel bookings cannot both
      pass the cap.
- [ ] Three history tables written transactionally with their changes; `GET …/history` returns them to
      branch roles under branch reach; a test proves `audit:read` is not required and not granted.
- [ ] Roster form records a practitioner-scoped exception and, for a clinics manager, a branch-scoped
      one. Withdraw works against `DELETE`.
- [ ] Licence renewal opens in a `Modal` with focus management; shortening an expiry shows the impact
      list and is gated on acknowledgement.
- [ ] Licence Alerts renews inline; Branches Overview shows names.
- [ ] `<ChangeTimeline>` renders all three histories, bilingual AR/EN, axe clean.
- [ ] The five branch screens resolve through the fixture seam and are covered by screen-level tests and
      the axe sweep.
- [ ] All pre-existing branch-scope, booking, RLS and min-necessary suites still green; OpenAPI drift
      gate green.

---

## 5. Out of scope

- Network-wide capacity (a cap across all clinics for one physician). Different control, different owner.
- Controlled substances in clinic inventory — still blocked by `CHECK (is_controlled = false)` per D1.
- Moving reach from role names to grants. `BranchScopeModes.ModeFor` remains the seam phase 21 lands on;
  `BranchWriteScope` consumes the mode and does not widen that surface.
