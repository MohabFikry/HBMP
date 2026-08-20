# Portal selection & real user administration — design

> Date: 2026-08-10 · Apps: `apps/web`, `apps/design-system`, `services/identity`
> Related: [14-navigation-structure.md](../../../HBMP-Design/14-navigation-structure.md) ·
> [40-user-access-model.md](../../../HBMP-Design/40-user-access-model.md) ·
> [0B-DESIGN-SYSTEM-UI.md](../../../HBMP-Design/0B-DESIGN-SYSTEM-UI.md) ·
> ADR-0036 (in-app sign-in), 28.6 (self-service reset), 28.7 (an admin issues a link, never a password)

## Problem

Two gaps, coupled by one fact.

**Nobody can create a user.** `identity-service` has had the endpoints since 17.4 — create, set roles,
deactivate, issue a reset link — and the SPA has never called any of them. `AdminUsers` and
`MembershipRoster` both *display* people; neither can bring one into existence, change what they may do, or
help them back in when they are locked out. An administrator's only remedy today is a database seed.

**A user can hold only one portal.** `Session.role` is singular. `roleFromClaimRoles` takes a token that may
name four roles and returns the first by priority; the other three are discarded silently. `AppShell` renders
`portalForRole(session.role)` and `AppRouter` resolves every path against that one portal. So a clinics
manager who is also an org admin can reach exactly one of the two portals they were granted, and there is no
screen anywhere that would tell them the other exists.

The coupling: *granting somebody a second portal* is what makes a portal picker worth building, and a portal
picker is what makes granting a second portal mean anything. They ship together or neither is real.

## Decisions

| Question | Decision | Why |
|---|---|---|
| Sequencing | One spec, three phases: portal model → picker/switcher → user administration | The phases depend on each other in that order |
| Portal entitlement | **Derived from roles.** Role→portal is already 1:1 across 21 portals | No second authorization surface, no new claim, no migration; the admin UI presents portals and sends the issuer's role names |
| Email sign-in | Resolve email first, fall back to username; unique index on `NormalizedEmail` | Non-breaking for seeded and service accounts |
| Passwords | Admin **issues a reset link** only (28.7 stands); add self-service *change my password* | An administrator must never know a credential. The missing piece was never the admin's power — it was the user's |
| Active portal | Derived from the URL's base segment; `permissions` is the **union** of held roles | A deep link into a portal you legitimately hold should open it, not 403. The catalog already scopes each portal's nav to its own sections |
| Picker | A page at `/portals` | One URL, one implementation, back button works |
| Admin IA | Merge `Users & Roles` into `Users & Access` | Two sections about the same people is how they drift apart |

## Phase A — the portal model

### Catalog (`apps/web/src/portals/catalog.ts`)

```ts
export type ZoneKey = "operations" | "clinical" | "fulfillment";
export const ZONES: ReadonlyArray<{ key: ZoneKey; label: Localized; dot: string }>;
```

Rendered in the declared order: Operations & administration, Clinical & approvals, Fulfillment — lab,
imaging & pharmacy. `dot` names an **existing** token (`--accent`, `--st-info-fg`, `--st-part-fg`); no new
colour enters the system. The dot is `aria-hidden` and decorative — the zone heading above it carries the
meaning, so the grouping survives greyscale and colour blindness.

`PortalDef` gains:

- `zone: ZoneKey`
- `icon: IconName` — the 44px tile glyph
- `description: Localized` — two lines saying what the portal *does*, authored in both languages

Zone assignment:

- **Operations & administration** — reception, branch_coordinator, clinics_manager, beneficiary_mgmt,
  beneficiary_mgmt_supervisor, case_manager, call_center, claims_officer, finance, provider_admin,
  policy_admin, org_admin, super_admin
- **Clinical & approvals** — doctor, nurse, medical_approval, medical_director
- **Fulfillment** — lab, radiology, pharmacy, procedure_provider

New helpers: `portalsForRoles(roles): PortalDef[]` (catalog order, deduped) and `portalForBase(base)`.

### Session

`Session` gains `roles: readonly Role[]`. `role` stays as the **primary** — first by the existing `ROLE_MAP`
priority — so every existing call site keeps compiling and a single-portal user's behaviour is byte-identical
to today. `permissions` becomes the union across held roles.

`config.ts` gains `rolesFromClaimRoles(claims): Role[]` beside the existing single-role function, and
`issuerRoleFor(portalRole): string` — the inverse of `ROLE_MAP`. The inverse is load-bearing for Phase C: the
issuer's catalog names `lab_tech`, `pharmacist`, `radiology_tech`, `network_team`, and an admin screen that
POSTed the portal keys `lab`/`pharmacy`/`radiology`/`provider_admin` would get a 422 for every clinical role
in the system.

### Routing

`useActivePortal()` reads `pathname.split("/")[1]` and matches it against the user's entitled portals,
falling back to the primary. `ResolveRoute` resolves a path against the portal that **owns** it rather than
against the caller's primary; a path in a portal the caller does not hold remains an audited 403.

`useHomePath()`: two or more portals → `/portals`; exactly one → that portal's first accessible section,
unchanged.

`AuthProvider.login` takes `Role[]`. `DevLoginForm` becomes a multi-select so the picker is exercisable with
no backend, which is how the whole frontend suite runs.

## Phase B — the picker (`/portals`)

`src/portals/PortalPicker.tsx`, rendered outside `AppShell` — no rail, no app bar, nothing to switch yet.

- **Header row**: `<Logo variant="lockup">`, then EN/ع, theme and sign-out at the inline end.
- **Greeting**: `Welcome back, {firstName}` at 30px/600. `firstName` is the first token of `displayName` with
  a leading honorific stripped (`Dr.`, `Nurse`, `د.`) — "Welcome back, Dr." is not a greeting. One-line lede:
  each portal shows only the data that role permits.
- **Zones** render in fixed order, and only when the user holds a portal in them. The label is uppercase with
  `--tbl-head-tracking`, followed by a `1px --border` hairline filling the remaining width.
- **Grid**: `repeat(auto-fill, minmax(268px, 1fr))`, `gap: var(--sp4)`.
- **Card** (a `<button>`): 44px `--accent-tint` icon tile · name 16px/600 · two-line description · footer meta
  of zone dot + section count. The count is of sections **this caller's permissions allow**, not the catalog
  total — a number that is true for somebody else is worse than no number.
- Rest `--surface-1` / `1px --border` / `--elev-1`; hover `translateY(-2px)` + `--accent` border +
  `--elev-hover`; `:focus-visible` takes the app's 3px ring. The lift is suppressed under
  `prefers-reduced-motion`.

Only entitled portals render. Visiting `/portals` with exactly one portal redirects into it.

## Phase C — the switcher

`src/shell/PortalSwitcher.tsx` — **one component**, rendered by `AppShell`, identical in every portal.
`NavRail` gains an optional `header?: ReactNode` slot rendered inside `<nav>` above the groups; that is the
whole design-system change, and it is what makes "one shared component, not per-portal copies" structural
rather than a convention.

Full-width button: icon tile, current portal name, "Change portal" sub-label, switch glyph at the inline end.
`1px --border`, `--surface-1`, `--r-md`, hover border `--accent`, ≥44px. It navigates to `/portals`; language
and theme live in `ThemeProvider` and survive the trip.

It is hidden below two portals. A control that goes nowhere is worse than no control — and "present in every
portal" is a statement about there being one implementation, not about showing a dead button to the majority
of users who hold a single portal.

The section list beneath is already `NavRail`'s job: it groups by catalog heading and sets
`aria-current="page"`. The accent text + tint + 4px inline-start bar are verified against the existing
`.mrs-navi[aria-current]` rule and fixed there if absent, rather than reimplemented.

## Phase D — Users & Access

`Users & Roles` leaves the `org_admin` and `super_admin` catalogs; `/admin/access` becomes the single surface
for people. Nothing is lost: account state and 2FA move into the user detail, and the role→scope matrix and
SoD table move to a **Governance** tab on the same screen.

- **List** — search by name/email, filter by status and portal. Name · Email · Portals · 2FA · Status.
- **Create** (modal) — full name, **email** (required, unique), username (defaults to the email, editable for
  service accounts), tenant, optional provider, and portal checkboxes **grouped by the same three zones**.
  On save: create → assign roles → issue a reset link. The administrator never chooses a password and is told
  so on the form.
- **Detail tabs** — Identity · Portals · Reach (branch grants) · Exceptions (overrides) · Sessions ·
  Effective access. The last four already exist and are reused unchanged.
- **Actions** — Send password-reset link · Deactivate · Reactivate. Each confirms, announces the outcome via
  `aria-live`, and is audited server-side.
- **Self-service** — "Change password" in `UserPane`: current + new + confirm, revoking the user's other
  sessions on success.

## Phase G — the access catalogue, custom roles, and exception grants

Added after the first four phases, and it is the piece that makes the rest honest.

**The problem.** Every permission on the platform has been data since 17.1 — rows in `identity.scope` — and
no screen ever listed them. An administrator could assign a role, and could grant an exception naming a key,
but had no way to discover what keys exist or what any of them means. The exception dialog's permission field
was free text. So the only workable strategy in front of a real person with an unusual job was *grant the
nearest bigger role* — which is how least privilege actually erodes: not by anyone arguing against it, but by
the alternative being unavailable at the moment of the decision.

**`/admin/policies` becomes the Access Catalogue**, in four tabs:

- **Permissions** — every scope, searchable, with its area, what it allows, which roles already hold it in
  this tenant, and its flags (service-only, superseded, platform-administration).
- **Roles** — built-in and the tenant's own, with what each actually grants, and **Design a role**: a name,
  a purpose, a sensitivity tier, and a permission picker grouped by domain.
- **Assignments** — the role bindings and recertification dates carried over from the screen 28.8 merged
  away. It is the only part of that screen not superseded, and dropping it would have silently removed
  recertification dates from the product.
- **Separated duties** — the SoD matrix, which is what refuses the combinations the designer offers.

**Custom roles** get an `owner_tenant_id` on `identity.role` (migration 0036). Names stay globally unique —
Identity's `RoleStore` requires it and the token's `roles` claim has nowhere to put a qualifier — so the
column records *ownership*, which is what lets one tenant's role be refused to another. A custom role grants
**permissions, never a portal**: the SPA derives a workspace from the built-in role→portal map, so a custom
role adds keys to whatever portal its holder already has. Somebody holding only custom roles has no portal
and lands on the fail-closed page, which is correct — a workspace is a designed thing with screens in it.

**Guard rails on the designer**, each enforced server-side and surfaced in the form:

| Rule | Why |
|---|---|
| Name matches `^[a-z][a-z0-9_]{2,48}$` | It lands in the `roles` claim, which other code splits on |
| Built-in names are reserved | Redefining `doctor` changes what the audit trail means |
| Service-only keys are refused | A machine credential on a person is invisible to any review |
| SoD is evaluated over the **set** | A role holding both halves of a split duty breaches it for *everyone* ever assigned it — and the existing per-key check passes each key against an empty held-set, so it finds nothing |

`SegregationOfDuties.EvaluateScopeSet` is the new pure function behind that last row.

**Exception grants** already existed (`POST /memberships/{id}/overrides`, SoD-guarded, reason mandatory). The
change is that its permission field is now a combobox over the same catalogue, searchable by what a key
*does* rather than by its spelling — an administrator looking for "let them upload a result" does not know it
is `lab:result:write`.

New endpoints: `GET /identity/admin/scopes`, `GET /identity/admin/roles`, `POST /identity/admin/roles`. The
existing `POST /roles/{role}/scopes` is relaxed to accept a custom role owned by the caller's tenant, and
refuses another tenant's with 403.

## Phase E — identity-service

| Change | Why |
|---|---|
| Sign-in resolves `FindByEmailAsync`, then `FindByNameAsync` | Email login; seeded and service accounts keep working |
| Email required and format-checked on create; unique index on `NormalizedEmail`; 409 on conflict | An email login needs an unambiguous email |
| `POST /identity/admin/users/{id}/reactivate` | Deactivate exists with no way back |
| `POST /identity/admin/users/{id}` (displayName, email) | Fixing a typo in an email currently means a second account |
| `POST /connect/session/password` (change own; current password required) | The real gap behind "change password" |

Failed sign-in attempts keep recording the **coarse** reason, so email-versus-username cannot become an
enumeration oracle. The migration is expand/contract: backfill emails, add the unique index, then enforce.

## Testing

- Portal model: primary-role parity for single-role users; union permissions; `portalsForRoles` ordering;
  `issuerRoleFor` round-trips every catalog role into `IdentityContract.Roles`.
- Picker: renders only entitled portals; skips itself at one portal; section counts reflect permissions;
  axe clean; RTL mirroring.
- Switcher: identical markup across portals; hidden below two; returns to the picker preserving lang/theme.
- Routing: a portal path the caller does not hold still 403s and is still audited.
- Admin: create → assign → reset-link → deactivate → reactivate against the dev API client; a11y on the
  modal.
- Backend (`./dotnet.sh test --with-db`): email-login resolution, email uniqueness, reactivate,
  self-change-password revoking other sessions, and the enumeration-oracle guard.
- `css-classes-exist` stays green — every new class gets a rule.

## Assumptions flagged

- Portal descriptions are 21 × 2 languages of new copy, authored from each portal's actual section list. The
  Arabic needs a native review pass before release.
- `beneficiary_mgmt_supervisor` and `clinics_manager` sit in Operations & administration despite supervising
  clinical work; they administer people and clinics, not patients.
