# @mersal/web

Mersal HBMP role portals (Phase 9.2). A single React app that presents **a distinct, code-split portal per
role** over the shared `@mersal/design-system`, with **permission-driven routing** so a user never sees a
route or menu item they cannot use (US-070 / US-071). Phase 9.3 fills specific sections with their wired
flagship screens; the shell, auth, routing, and min-necessary navigation are live now.

## What's here

- **Auth (`src/auth/`)** — OIDC + MFA sign-in that lands the user on **their** portal only. `AuthClient` is
  an interface; `DevAuthClient` (a role picker + 6-digit MFA step, no live Keycloak needed) implements the
  same shape a real OIDC client will, so the shell/routing/session logic are backend-agnostic. Session has
  an absolute TTL with an **idle warning + re-auth prompt** before expiry.
- **Authorization (`src/authz/permissions.ts`)** — the UI mirror of `11-permission-matrix.md`: a permission
  catalog + `role → permissions` map. This drives which routes mount and which menu items render. The six
  min-necessary hard rules are structural (Reception has no `emr.*`, Finance no clinical/diagnosis, Pharmacy
  no `lab.result`, Lab no `prescriptions.*`). **The server remains the source of truth and re-authorizes
  every call.**
- **Portal catalog (`src/portals/catalog.ts`)** — the 11 portals from `14-navigation-structure §2`, each a
  set of permission-gated sections with bilingual (en/ar) labels + icons.
- **Shell (`src/shell/AppShell.tsx`)** — glass top bar (`banner`), **permission-generated** nav rail
  (`navigation`), breadcrumb (`Portal ▸ Section`), `main` landmark, global keyboard map (`/` search,
  `g h` home, `g q` primary queue), and the session-timeout modal. Nav shows only the sections the user may
  use.
- **Routing (`src/routing/`)** — the router mounts only usable routes; a forbidden deep link resolves to an
  **audited 403** (`access.denied` via `src/audit/auditClient.ts`) with a *request-access* affordance,
  while an unknown path is a 404 — never a blank screen.

## Acceptance (US-070 / US-071) — covered by tests

- Valid credentials + MFA → land only on the role's portal.
- Navigation shows only routes/data the role allows (e.g. Finance sees no diagnoses; the nav hides other
  portals' items).
- A forbidden deep link → 403 with request-access, **audited**.
- Unauthenticated visitor → redirected to sign-in; inactivity → warned + re-authenticated.

## Scripts

| Command | What it does |
|---------|--------------|
| `pnpm --filter @mersal/web dev` | Vite dev server |
| `pnpm --filter @mersal/web test` | Vitest — routing + permission-gating + **axe** a11y |
| `pnpm --filter @mersal/web build` | `tsc --noEmit` + Vite build |
| `pnpm --filter @mersal/web lint` | Type-check only |

> Node 20 ⇒ use **pnpm 9** (`npx pnpm@9.15.9 …`). CI: `.github/workflows/frontend-ci.yml` (web job:
> lint → test → build).

## Dev sign-in

The dev auth stub shows a **role picker** (standing in for the IdP account) and accepts **any 6-digit MFA
code**. Pick a role, enter e.g. `123456`, and you land on that role's portal. Deep-link to another portal's
route (e.g. `/finance/settlements` while signed in as Reception) to see the audited 403.
